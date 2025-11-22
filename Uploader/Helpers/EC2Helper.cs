using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;

namespace Uploader.Helpers
{
    /// <summary>
    /// Helper for rebooting EC2 instances by environment tag and checking their health.
    /// </summary>
    public class EC2Helper
    {
        private readonly string _environmentName;
        private readonly AmazonEC2Client _ec2Client;

        public EC2Helper(RegionEndpoint regionEndpoint, string environmentName)
        {
            _ec2Client = new AmazonEC2Client(regionEndpoint);
            _environmentName = environmentName;
        }

        public async Task<bool> RebootInstancesRequest(Action<string>? statusCallback = null)
        {
            if (string.IsNullOrEmpty(_environmentName))
                return false;

            var instanceIds = await GetInstanceIdsByTagAsync(_ec2Client, "Name", _environmentName);
            if (!instanceIds.Any())
            {
                statusCallback?.Invoke($"No instances found for environment '{_environmentName}'.\n");
                return false;
            }

            bool success = await RebootInstancesAsync(_ec2Client, instanceIds, statusCallback);
            statusCallback?.Invoke(success
                ? "All instances rebooted successfully.\n"
                : "Failed to reboot one or more instances.\n");

            return success;
        }

        /// <summary>
        /// Retrieves instance IDs filtered by a specific tag key-value pair.
        /// </summary>
        private static async Task<List<string>> GetInstanceIdsByTagAsync(
            AmazonEC2Client ec2Client,
            string tagKey,
            string tagValue)
        {
            var describeRequest = new DescribeInstancesRequest
            {
                Filters = new List<Filter>
                {
                    new Filter($"tag:{tagKey}", new List<string> { tagValue })
                }
            };

            var response = await ec2Client.DescribeInstancesAsync(describeRequest).ConfigureAwait(false);

            return response.Reservations
                .SelectMany(r => r.Instances)
                .Where(i => i.State.Name != InstanceStateName.Terminated &&
                            i.State.Name != InstanceStateName.Stopped)
                .Select(i => i.InstanceId)
                .ToList();
        }

        /// <summary>
        /// Reboots the specified EC2 instances and waits for them to reach the running state.
        /// </summary>
        private static async Task<bool> RebootInstancesAsync(
            AmazonEC2Client ec2Client,
            List<string> instanceIds,
            Action<string>? statusCallback = null)
        {
            try
            {
                var rebootRequest = new RebootInstancesRequest
                {
                    InstanceIds = instanceIds
                };

                await ec2Client.RebootInstancesAsync(rebootRequest).ConfigureAwait(false);
                statusCallback?.Invoke($"Reboot request sent for instances: {string.Join(", ", instanceIds)}.\n");

                // Wait for all instances to return to running state
                var tasks = instanceIds
                    .Select(id => WaitForInstanceStateAsync(ec2Client, id, InstanceStateName.Running, statusCallback));
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                if (results.All(r => r))
                {
                    statusCallback?.Invoke("All instances are running. Checking health...\n");
                    var healthTasks = instanceIds.Select(id => IsInstanceRestartedAndHealthyAsync(ec2Client, id, statusCallback));
                    var healthResults = await Task.WhenAll(healthTasks).ConfigureAwait(false);
                    return healthResults.All(h => h);
                }

                statusCallback?.Invoke("Some instances failed to reach running state.\n");
                return false;
            }
            catch (AmazonEC2Exception ex)
            {
                statusCallback?.Invoke($"EC2 error: {ex.Message}\n");
                return false;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"Unexpected error: {ex.Message}\n");
                return false;
            }
        }

        /// <summary>
        /// Polls the instance state until it matches the specified state.
        /// </summary>
        private static async Task<bool> WaitForInstanceStateAsync(
            AmazonEC2Client ec2Client,
            string instanceId,
            InstanceStateName targetState,
            Action<string>? statusCallback = null)
        {
            var describeRequest = new DescribeInstancesRequest
            {
                InstanceIds = new List<string> { instanceId }
            };

            bool inTargetState = false;
            const int maxRetries = 60; // e.g. 5 minutes at 5-second intervals
            int retryCount = 0;

            while (!inTargetState && retryCount < maxRetries)
            {
                var response = await ec2Client.DescribeInstancesAsync(describeRequest).ConfigureAwait(false);
                var instance = response.Reservations[0].Instances[0];
                inTargetState = instance.State.Name == targetState;

                if (!inTargetState)
                {
                    statusCallback?.Invoke(".");
                    await Task.Delay(5000).ConfigureAwait(false);
                    retryCount++;
                }
            }

            statusCallback?.Invoke("\n");
            return inTargetState;
        }

        /// <summary>
        /// Checks if an EC2 instance is running and healthy (instance + system status checks OK).
        /// </summary>
        public static async Task<bool> IsInstanceRestartedAndHealthyAsync(
            AmazonEC2Client ec2Client,
            string instanceId,
            Action<string>? statusCallback = null)
        {
            try
            {
                // Check instance state
                var stateRequest = new DescribeInstancesRequest
                {
                    InstanceIds = new List<string> { instanceId }
                };
                var stateResponse = await ec2Client.DescribeInstancesAsync(stateRequest).ConfigureAwait(false);
                var instance = stateResponse.Reservations.SelectMany(r => r.Instances).FirstOrDefault();

                if (instance == null || instance.State.Name != InstanceStateName.Running)
                {
                    statusCallback?.Invoke($"Instance {instanceId} is not running.\n");
                    return false;
                }

                // Check instance and system status
                var statusRequest = new DescribeInstanceStatusRequest
                {
                    InstanceIds = new List<string> { instanceId },
                    IncludeAllInstances = true
                };
                var statusResponse = await ec2Client.DescribeInstanceStatusAsync(statusRequest).ConfigureAwait(false);
                var status = statusResponse.InstanceStatuses.FirstOrDefault(s => s.InstanceId == instanceId);

                if (status == null)
                {
                    statusCallback?.Invoke($"No status found for instance {instanceId}.\n");
                    return false;
                }

                bool isHealthy =
                    status.InstanceState.Name == InstanceStateName.Running &&
                    status.SystemStatus.Status == SummaryStatus.Ok &&
                    status.Status.Status == SummaryStatus.Ok;

                if (isHealthy)
                {
                    statusCallback?.Invoke($"Instance {instanceId} is running and healthy.\n");
                }
                else
                {
                    statusCallback?.Invoke(
                        $"Instance {instanceId} is running but not healthy. " +
                        $"System Status: {status.SystemStatus.Status}, Instance Status: {status.Status.Status}\n");
                }

                return isHealthy;
            }
            catch (AmazonEC2Exception ex)
            {
                statusCallback?.Invoke($"EC2 error checking instance {instanceId}: {ex.Message}\n");
                return false;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"Unexpected error checking instance {instanceId}: {ex.Message}\n");
                return false;
            }
        }
    }
}

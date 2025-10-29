// EC2Helper.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;

namespace Uploader.Helpers
{
    class EC2Helper
    {
        string m_strEnvironmentName;

        AmazonEC2Client ec2Client;
        public EC2Helper(RegionEndpoint regionEndpoint, string environmentName)
        {
            ec2Client = new AmazonEC2Client(regionEndpoint);
            m_strEnvironmentName = environmentName;
        }

        public async Task RebootInstancesRequest(Action<string> statusCallback = null)
        {
            if (string.IsNullOrEmpty(m_strEnvironmentName))
            {
                return;
            }
            var instanceIds = await GetInstanceIdsByTagAsync(ec2Client, "Name", m_strEnvironmentName);
            if (!instanceIds.Any())
            {
                statusCallback?.Invoke($"No instances found for environment '{m_strEnvironmentName}'.\n");
                return;
            }

            bool success = await RebootInstancesAsync(ec2Client, instanceIds, statusCallback);
            statusCallback?.Invoke(success ? "All instances rebooted successfully.\n" : "Failed to reboot one or more instances.\n");
        }

        /// <summary>
        /// Retrieves instance IDs filtered by a specific tag key-value pair.
        /// </summary>
        /// <param name="ec2Client">The AmazonEC2Client instance.</param>
        /// <param name="tagKey">The tag key (e.g., "Environment").</param>
        /// <param name="tagValue">The tag value (e.g., "dev").</param>
        /// <returns>A task that represents the asynchronous operation, returning a list of matching instance IDs.</returns>
        private static async Task<List<string>> GetInstanceIdsByTagAsync(AmazonEC2Client ec2Client, string tagKey, string tagValue)
        {
            var describeRequest = new DescribeInstancesRequest
            {
                Filters = new List<Filter>
                {
                    new Filter($"tag:{tagKey}", new List<string> { tagValue })
                }
            };

            var response = await ec2Client.DescribeInstancesAsync(describeRequest);
            return response.Reservations.SelectMany(r => r.Instances)
                .Where(i => i.State.Name != InstanceStateName.Terminated && i.State.Name != InstanceStateName.Stopped)
                .Select(i => i.InstanceId)
                .ToList();
        }

        /// <summary>
        /// Reboots the specified EC2 instances and waits for them to reach the running state.
        /// </summary>
        /// <param name="ec2Client">The AmazonEC2Client instance.</param>
        /// <param name="instanceIds">The list of instance IDs to reboot.</param>
        /// <returns>A task that represents the asynchronous operation, returning true on success.</returns>
        static async Task<bool> RebootInstancesAsync(AmazonEC2Client ec2Client, List<string> instanceIds, Action<string> statusCallback = null)
        {
            try
            {
                var rebootRequest = new RebootInstancesRequest
                {
                    InstanceIds = instanceIds
                };

                await ec2Client.RebootInstancesAsync(rebootRequest);
                statusCallback?.Invoke($"Reboot request sent for instances: {string.Join(", ", instanceIds)}.\n");

                // Wait for all instances to return to running state
                var tasks = instanceIds.Select(id => WaitForInstanceStateAsync(ec2Client, id, InstanceStateName.Running, statusCallback));
                var results = await Task.WhenAll(tasks);

                if (results.All(r => r))
                {
                    statusCallback?.Invoke("All instances are running. Checking health...\n");
                    var healthTasks = instanceIds.Select(id => IsInstanceRestartedAndHealthyAsync(ec2Client, id, statusCallback));
                    var healthResults = await Task.WhenAll(healthTasks);
                    return healthResults.All(h => h);
                }
                else
                {
                    statusCallback?.Invoke("Some instances failed to reach running state.\n");
                    return false;
                }
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
        /// <param name="ec2Client">The AmazonEC2Client instance.</param>
        /// <param name="instanceId">The ID of the instance.</param>
        /// <param name="targetState">The target instance state.</param>
        /// <returns>A task that represents the asynchronous operation, returning true if the state is reached.</returns>
        private static async Task<bool> WaitForInstanceStateAsync(AmazonEC2Client ec2Client, string instanceId, InstanceStateName targetState, Action<string> statusCallback = null)
        {
            var describeRequest = new DescribeInstancesRequest
            {
                InstanceIds = new List<string> { instanceId }
            };

            bool inTargetState = false;
            int maxRetries = 60; // Adjust as needed (e.g., 5 minutes at 5-second intervals)
            int retryCount = 0;

            while (!inTargetState && retryCount < maxRetries)
            {
                var response = await ec2Client.DescribeInstancesAsync(describeRequest);
                var instance = response.Reservations[0].Instances[0];
                inTargetState = instance.State.Name == targetState;

                if (!inTargetState)
                {
                    statusCallback?.Invoke(".");
                    await Task.Delay(5000); // Use Task.Delay for async
                    retryCount++;
                }
            }

            statusCallback?.Invoke("\n"); // New line after progress dots
            return inTargetState;
        }

        /// <summary>
        /// Checks if the specified EC2 instance has restarted and is running well, including status checks.
        /// </summary>
        /// <param name="ec2Client">The AmazonEC2Client instance.</param>
        /// <param name="instanceId">The ID of the instance to check.</param>
        /// <param name="statusCallback">Optional callback for status messages.</param>
        /// <returns>A task that represents the asynchronous operation, returning true if the instance is running and healthy.</returns>
        public static async Task<bool> IsInstanceRestartedAndHealthyAsync(AmazonEC2Client ec2Client, string instanceId, Action<string> statusCallback = null)
        {
            try
            {
                // First, check if the instance is in the running state
                var stateRequest = new DescribeInstancesRequest
                {
                    InstanceIds = new List<string> { instanceId }
                };
                var stateResponse = await ec2Client.DescribeInstancesAsync(stateRequest);
                var instance = stateResponse.Reservations.SelectMany(r => r.Instances).FirstOrDefault();
                if (instance == null || instance.State.Name != InstanceStateName.Running)
                {
                    statusCallback?.Invoke($"Instance {instanceId} is not running.\n");
                    return false;
                }

                // Then, check the instance status (system and instance checks)
                var statusRequest = new DescribeInstanceStatusRequest
                {
                    InstanceIds = new List<string> { instanceId },
                    IncludeAllInstances = true // Ensures we get status even if not running, but we already checked running
                };
                var statusResponse = await ec2Client.DescribeInstanceStatusAsync(statusRequest);
                var status = statusResponse.InstanceStatuses.FirstOrDefault(s => s.InstanceId == instanceId);

                if (status == null)
                {
                    statusCallback?.Invoke($"No status found for instance {instanceId}.\n");
                    return false;
                }

                bool isHealthy = status.InstanceState.Name == InstanceStateName.Running &&
                                  status.SystemStatus.Status == SummaryStatus.Ok &&
                                  status.Status.Status == SummaryStatus.Ok;

                if (isHealthy)
                {
                    statusCallback?.Invoke($"Instance {instanceId} is running and healthy.\n");
                }
                else
                {
                    statusCallback?.Invoke($"Instance {instanceId} is running but not healthy. System Status: {status.SystemStatus.Status}, Instance Status: {status.Status.Status}\n");
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
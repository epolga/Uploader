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

        public async Task RebootInstancesRequest()
        {
            if (string.IsNullOrEmpty(m_strEnvironmentName))
            {
                return;
            }
            var instanceIds = await GetInstanceIdsByTagAsync(ec2Client, "Name", m_strEnvironmentName);
            if (!instanceIds.Any())
            {
                Console.WriteLine($"No instances found for environment '{m_strEnvironmentName}'.");
                return;
            }

            bool success = await RebootInstancesAsync(ec2Client, instanceIds);
            Console.WriteLine(success ? "All instances rebooted successfully." : "Failed to reboot one or more instances.");
        }
        /// <summary>
        /// Retrieves instance IDs filtered by a specific tag key-value pair.
        /// </summary>
        /// <param name="ec2Client">The AmazonEC2Client instance.</param>
        /// <param name="tagKey">The tag key (e.g., "Environment").</param>
        /// <param name="tagValue">The tag value (e.g., "dev").</param>
        /// <returns>A task that represents the asynchronous operation, returning a list of matching instance IDs.</returns>
        private async Task<List<string>> GetInstanceIdsByTagAsync(AmazonEC2Client ec2Client, string tagKey, string tagValue)
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
        static async Task<bool> RebootInstancesAsync(AmazonEC2Client ec2Client, List<string> instanceIds)
        {
            try
            {
                var rebootRequest = new RebootInstancesRequest
                {
                    InstanceIds = instanceIds
                };

                await ec2Client.RebootInstancesAsync(rebootRequest);
                Console.WriteLine($"Reboot request sent for instances: {string.Join(", ", instanceIds)}.");

                // Wait for all instances to return to running state
                var tasks = instanceIds.Select(id => WaitForInstanceStateAsync(ec2Client, id, InstanceStateName.Running));
                var results = await Task.WhenAll(tasks);
                return results.All(r => r);
            }
            catch (AmazonEC2Exception ex)
            {
                Console.WriteLine($"EC2 error: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex.Message}");
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
        private static async Task<bool> WaitForInstanceStateAsync(AmazonEC2Client ec2Client, string instanceId, InstanceStateName targetState)
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
                    Console.Write("."); // Progress indicator
                    Thread.Sleep(5000); // Wait 5 seconds before retrying
                    retryCount++;
                }
            }

            Console.WriteLine(); // New line after progress dots
            return inTargetState;
        }
    }
}
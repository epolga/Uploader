using System;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.ElasticBeanstalk;
using Amazon.ElasticBeanstalk.Model;

namespace Uploader.Helpers
{
  /// <summary>
  /// Helper for restarting Elastic Beanstalk application servers for a specific environment.
  /// </summary>
  public class ElasticBeanstalkHelper
  {
    private readonly AmazonElasticBeanstalkClient _elasticBeanstalkClient;
    private readonly string _environmentName;

    public ElasticBeanstalkHelper(RegionEndpoint regionEndpoint, string environmentName)
    {
      _elasticBeanstalkClient = new AmazonElasticBeanstalkClient(regionEndpoint);
      _environmentName = environmentName;
    }

    public async Task<bool> RestartEnvironmentAsync(Action<string>? statusCallback = null)
    {
      if (string.IsNullOrWhiteSpace(_environmentName))
      {
        statusCallback?.Invoke("Elastic Beanstalk environment name is not configured.\n");
        return false;
      }

      try
      {
        var describeResponse = await _elasticBeanstalkClient.DescribeEnvironmentsAsync(
            new DescribeEnvironmentsRequest
            {
              EnvironmentNames = new System.Collections.Generic.List<string> { _environmentName },
              IncludeDeleted = false
            }).ConfigureAwait(false);

        var environment = describeResponse.Environments
            .FirstOrDefault(env => string.Equals(env.EnvironmentName, _environmentName, StringComparison.OrdinalIgnoreCase));

        if (environment == null)
        {
          statusCallback?.Invoke($"Elastic Beanstalk environment '{_environmentName}' was not found.\n");
          return false;
        }

        statusCallback?.Invoke(
            $"Restarting Elastic Beanstalk environment '{environment.EnvironmentName}' " +
            $"(status: {environment.Status}, health: {environment.Health ?? "unknown"}).\n");

        await _elasticBeanstalkClient.RestartAppServerAsync(
            new RestartAppServerRequest
            {
              EnvironmentName = environment.EnvironmentName
            }).ConfigureAwait(false);

        statusCallback?.Invoke("Elastic Beanstalk restart request submitted successfully.\n");
        return true;
      }
      catch (AmazonElasticBeanstalkException ex)
      {
        statusCallback?.Invoke($"Elastic Beanstalk error: {ex.Message}\n");
        return false;
      }
      catch (Exception ex)
      {
        statusCallback?.Invoke($"Unexpected error: {ex.Message}\n");
        return false;
      }
    }
  }
}
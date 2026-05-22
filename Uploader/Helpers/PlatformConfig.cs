using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Uploader.Helpers
{
    /// <summary>
    /// Reads cross-project shared settings from cross-stitch-platform-docs/platform-config.json.
    /// Location: PLATFORM_CONFIG_PATH env var if set, otherwise the sibling
    /// cross-stitch-platform-docs repo (found by walking up from the running assembly).
    /// </summary>
    public static class PlatformConfig
    {
        private const string PlatformDocsRepoName = "cross-stitch-platform-docs";
        private const string ConfigFileName = "platform-config.json";

        public static string ResolvePinterestTokenPath()
        {
            var configPath = LocateConfigFile();
            var configRaw = File.ReadAllText(configPath);

            string? tokenPath;
            try
            {
                var config = JObject.Parse(configRaw);
                tokenPath = (string?)config["pinterestTokenPath"];
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse platform-config.json at {configPath}", ex);
            }

            if (string.IsNullOrWhiteSpace(tokenPath))
                throw new InvalidOperationException(
                    $"pinterestTokenPath is not set in {configPath}");

            if (Path.IsPathRooted(tokenPath))
                return Path.GetFullPath(tokenPath);

            // Relative paths resolve against the workspace root — the parent of the platform-docs repo.
            var workspaceRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath))
                ?? throw new InvalidOperationException(
                    $"Could not determine workspace root from {configPath}");

            return Path.GetFullPath(Path.Combine(workspaceRoot, tokenPath));
        }

        private static string LocateConfigFile()
        {
            var envOverride = Environment.GetEnvironmentVariable("PLATFORM_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                if (!File.Exists(envOverride))
                    throw new FileNotFoundException(
                        $"PLATFORM_CONFIG_PATH points to a file that does not exist: {envOverride}");
                return Path.GetFullPath(envOverride);
            }

            // Walk up from the running assembly looking for a sibling cross-stitch-platform-docs repo.
            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, PlatformDocsRepoName, ConfigFileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
                current = current.Parent;
            }

            throw new FileNotFoundException(
                $"Could not locate {ConfigFileName}. Set PLATFORM_CONFIG_PATH or place the " +
                $"{PlatformDocsRepoName} repo alongside the calling project.");
        }
    }
}

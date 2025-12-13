using System;
using System.Collections.Generic;
using System.Configuration;
using System.Text;
using UploadPatterns;

namespace Uploader.Helpers
{
    /// <summary>
    /// Builds site and image URLs for patterns so they can be reused across email
    /// and Pinterest flows.
    /// </summary>
    public class PatternLinkHelper
    {
        private readonly string _siteBaseUrl;
        private readonly string _imageBaseUrl;
        private readonly string _photoPrefix;

        public PatternLinkHelper()
        {
            _siteBaseUrl = GetSiteBaseUrlFromConfig();

            var configuredImageBase =
                ConfigurationManager.AppSettings["S3PublicBaseUrl"];

            if (!string.IsNullOrWhiteSpace(configuredImageBase))
            {
                _imageBaseUrl = configuredImageBase.TrimEnd('/');
            }
            else
            {
                var bucketName =
                    ConfigurationManager.AppSettings["S3BucketName"] ??
                    "cross-stitch-designs";

                _imageBaseUrl = $"https://{bucketName}.s3.amazonaws.com";
            }

            _photoPrefix =
                ConfigurationManager.AppSettings["S3PhotoPrefix"] ??
                "photos";
        }

        public string SiteBaseUrl => _siteBaseUrl;

        private static string GetSiteBaseUrlFromConfig()
        {
            string siteBaseUrl =
                ConfigurationManager.AppSettings["SiteBaseUrl"] ??
                ConfigurationManager.AppSettings["PinterestLinkUrl"] ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(siteBaseUrl))
            {
                throw new ConfigurationErrorsException(
                    "SiteBaseUrl must be configured in appSettings.");
            }

            return siteBaseUrl.TrimEnd('/');
        }

        public string BuildPatternUrl(PatternInfo patternInfo)
        {
            if (patternInfo == null) throw new ArgumentNullException(nameof(patternInfo));

            string caption = (patternInfo.Title ?? "Cross-stitch-pattern").Replace(' ', '-');
            int.TryParse(patternInfo.NPage, out int nPage);
            string baseUrl = _siteBaseUrl;

            return $"{baseUrl}/{caption}-{patternInfo.AlbumId}-{nPage-1}-Free-Design.aspx";
        }

        public string BuildImageUrl(int designId, int albumId, string photoFileName = "4.jpg")
        {
            return $"{_imageBaseUrl}/{_photoPrefix}/{albumId}/{designId}/{photoFileName}";
        }

        public string BuildAlbumUrl(string albumId, string? caption = null)
        {
            if (string.IsNullOrWhiteSpace(albumId))
                throw new ArgumentException("AlbumId must be provided.", nameof(albumId));

            string template = ConfigurationManager.AppSettings["AlbumUrlTemplate"] ?? string.Empty;
            string baseUrl = _siteBaseUrl;
            string slug = BuildAlbumCaptionSlug(caption, albumId);

            if (!string.IsNullOrWhiteSpace(template))
            {
                return template
                    .Replace("{AlbumId}", albumId)
                    .Replace("{CaptionSlug}", slug);
            }

            return $"{baseUrl}/Free-{slug}-Charts.aspx";
        }

        private static string BuildAlbumCaptionSlug(string? caption, string albumId)
        {
            if (string.IsNullOrWhiteSpace(caption))
                return $"Album-{albumId}";

            var parts = new List<string>();
            var current = new StringBuilder();

            foreach (char c in caption)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
            }

            if (current.Length > 0)
                parts.Add(current.ToString());

            if (parts.Count == 0)
                return $"Album-{albumId}";

            for (int i = 0; i < parts.Count; i++)
            {
                string word = parts[i].ToLowerInvariant();
                parts[i] = char.ToUpperInvariant(word[0]) + word.Substring(1);
            }

            return string.Join("-", parts);
        }
    }
}

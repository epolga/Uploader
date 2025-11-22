using System;
using System.Configuration;
using System.Globalization;
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
            _siteBaseUrl =
                ConfigurationManager.AppSettings["SiteBaseUrl"] ??
                ConfigurationManager.AppSettings["PinterestLinkUrl"] ??
                "https://www.cross-stitch-pattern.net";

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

        public string BuildPatternUrl(PatternInfo patternInfo)
        {
            if (patternInfo == null) throw new ArgumentNullException(nameof(patternInfo));

            string caption = (patternInfo.Title ?? "Cross-stitch-pattern").Replace(' ', '-');
            int.TryParse(patternInfo.NPage, out int nPage);
            string baseUrl = _siteBaseUrl.TrimEnd('/');

            return $"{baseUrl}/{caption}-{patternInfo.AlbumId}-{nPage}-Free-Design.aspx";
        }

        public string BuildImageUrl(int designId, int albumId, string photoFileName = "4.jpg")
        {
            return $"{_imageBaseUrl}/{_photoPrefix}/{albumId}/{designId}/{photoFileName}";
        }
    }
}

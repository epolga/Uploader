using System;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UploadPatterns;

namespace Uploader.Helpers
{
    /// <summary>
    /// Helper for creating a Pinterest pin from an S3-hosted image
    /// and your cross-stitch design metadata.
    /// </summary>
    public class PinterestHelper
    {
        private readonly string _boardId;
        private readonly string _bucketName;
        private readonly string _photoPrefix;
        private readonly string _siteBaseUrl;
        private readonly string _pinsEndpoint;

        private readonly PinterestOAuthClient _pinterestOAuthClient;

        public PinterestHelper()
        {
            _boardId = ConfigurationManager.AppSettings["PinterestBoardId"] ?? string.Empty;
            if (string.IsNullOrEmpty(_boardId))
            {
                throw new InvalidOperationException(
                    "Pinterest board ID not configured in App.config (key: PinterestBoardId).");
            }

            _bucketName = ConfigurationManager.AppSettings["S3BucketName"] ?? "cross-stitch-designs";
            _photoPrefix = ConfigurationManager.AppSettings["S3PhotoPrefix"] ?? "images/designs/photos";
            _siteBaseUrl = ConfigurationManager.AppSettings["PinterestLinkUrl"] ??
                           "https://www.cross-stitch-pattern.net";
            _pinsEndpoint = ConfigurationManager.AppSettings["PinterestPinsUrl"] ??
                            "https://api.pinterest.com/v5/pins";

            _pinterestOAuthClient = new PinterestOAuthClient();
        }

        /// <summary>
        /// Builds the relative URL to your pattern page based on pattern info.
        /// </summary>
        private string GetPatternUrl(PatternInfo patternInfo)
        {
            if (patternInfo == null) throw new ArgumentNullException(nameof(patternInfo));
            int nPage = int.TryParse(patternInfo.NPage, out int parsedPage) ? parsedPage - 1 : 0;
            string slugTitle = (patternInfo.Title ?? string.Empty).Replace(' ', '-');
            return $"{slugTitle}-{patternInfo.AlbumId}-{patternInfo.NPage}-Free-Design.aspx";
        }

        /// <summary>
        /// Creates a Pinterest pin using the image from S3 and your pattern metadata.
        /// Returns the created pin ID (or a message).
        /// </summary>
        public async Task<string> UploadPinExampleAsync(PatternInfo patternInfo)
        {
            if (patternInfo == null) throw new ArgumentNullException(nameof(patternInfo));

            // Build image URL in S3
            string photoKey = GetPhotoKey(patternInfo.DesignID, patternInfo.AlbumId);
            string imageUrl = $"https://{_bucketName}.s3.amazonaws.com/{photoKey}";

            // Build destination link to your site
            string patternUrl = $"{_siteBaseUrl}/{GetPatternUrl(patternInfo)}";

            // Obtain valid access token via OAuth
            string accessToken = await _pinterestOAuthClient.GetValidAccessTokenAsync().ConfigureAwait(false);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var payload = new
            {
                board_id = _boardId,
                link = patternUrl,                       // use actual pattern page link
                title = $"{patternInfo.Title} Cross Stitch Pattern – Easy Printable PDF",
                alt_text = $"{patternInfo.Description} for FREE download",
                description = patternInfo.Notes + "\nGet more printable cross stitch patterns at Cross-Stitch-Pattern.net.",
                media_source = new
                {
                    source_type = "image_url",
                    url = imageUrl
                }
            };

            string json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(_pinsEndpoint, content).ConfigureAwait(false);
            string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Pinterest pin upload failed: {response.StatusCode} - {responseContent}");

            var pinResponse = JsonConvert.DeserializeObject<PinResponse>(responseContent);
            Console.WriteLine("Pin created: " + responseContent);

            return pinResponse?.Id ?? "created (ID not found in response)";
        }

        private string GetPhotoKey(int designId, int albumId)
        {
            // Your images are always named "4.jpg" within each design folder
            string photoFileName = "4.jpg";
            return $"{_photoPrefix}/{albumId}/{designId}/{photoFileName}";
        }

        private class PinResponse
        {
            [JsonProperty("id")]
            public string Id { get; set; } = string.Empty;
        }
    }
}

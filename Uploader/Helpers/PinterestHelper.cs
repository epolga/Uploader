// Uploader/Helpers/PinterestHelper.cs
// Production-ready Pinterest helper using image_url source type.
//
// Notes:
// - Uses Pinterest API v5 production endpoint: https://api.pinterest.com/v5
// - Expects valid OAuth access token and board ID in App.config:
//     PinterestAccessToken, PinterestBoardId
// - Creates a pin from a public image URL (e.g. your S3 image).

using Amazon;
using System;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UploadPatterns; // PatternInfo

namespace Uploader.Helpers
{
    public class PinterestHelper
    {
        private readonly string accessToken;
        private readonly string boardId;

        // Production Pinterest API base URL
        private readonly string baseUrl = "https://api.pinterest.com/v5";

        public PinterestHelper()
        {
            accessToken = ConfigurationManager.AppSettings["PinterestAccessToken"] ?? string.Empty;
            boardId = ConfigurationManager.AppSettings["PinterestBoardId"] ?? string.Empty;

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(boardId))
            {
                throw new InvalidOperationException(
                    "Pinterest access token or board ID not configured in App.config (keys: PinterestAccessToken, PinterestBoardId).");
            }
        }

        /// <summary>
        /// Creates a Pinterest pin on the configured board using an image URL.
        /// </summary>
        /// <param name="imageUrl">Public image URL (e.g. S3 HTTPS URL).</param>
        /// <param name="patternInfo">Design metadata used for title/description.</param>
        /// <returns>Pin ID string, or a descriptive message if ID is missing.</returns>
        public async Task<string> CreatePinAsync(string imageUrl, PatternInfo patternInfo)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                throw new ArgumentException("imageUrl must not be empty.", nameof(imageUrl));
            }

            if (patternInfo == null)
            {
                throw new ArgumentNullException(nameof(patternInfo));
            }

            var pinData = new
            {
                board_id = boardId,
                title = string.IsNullOrWhiteSpace(patternInfo.Title)
                    ? "Cross-stitch pattern"
                    : patternInfo.Title,
                description =
                    $"{patternInfo.Description ?? string.Empty}\n" +
                    $"Notes: {patternInfo.Notes ?? string.Empty}\n" +
                    $"Dimensions: {patternInfo.Width}x{patternInfo.Height}, Colors: {patternInfo.NColors}",
                media_source = new
                {
                    source_type = "image_url",
                    url = imageUrl
                }
            };

            var response = await SendRequestAsync(HttpMethod.Post, "/pins", pinData).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    // Some successful responses might return empty body
                    return "created (no ID returned in response body)";
                }

                try
                {
                    var pinResponse = JsonConvert.DeserializeObject<PinResponse>(responseContent);
                    return pinResponse?.id ?? "created (ID not found in response)";
                }
                catch (JsonException ex)
                {
                    throw new Exception(
                        $"Deserialization failed: {ex.Message}. Response content: {responseContent}");
                }
            }

            throw new Exception($"Pinterest API error: {response.StatusCode} - {responseContent}");
        }

        private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, object data)
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var request = new HttpRequestMessage(method, $"{baseUrl}{path}");

                if (data != null)
                {
                    var json = JsonConvert.SerializeObject(data);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                return await httpClient.SendAsync(request).ConfigureAwait(false);
            }
        }

        private class PinResponse
        {
            public string id { get; set; }
        }
    }
}

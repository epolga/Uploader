// Uploader/Helpers/PinterestHelper.cs
// Changes:
// - Added a private PinResponse class to strongly type the deserialization, avoiding issues with dynamic if the JSON structure is complex.
// - Added check for empty response content, returning a placeholder if no body is present (though expected for 201).
// - This should prevent deserialization failures if the content is empty or not a full object.

using Amazon;
using System;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UploadPatterns; // Assuming PatternInfo is in this namespace; adjust if needed

namespace Uploader.Helpers
{
    public class PinterestHelper
    {
        private readonly string accessToken;
        private readonly string boardId;
        private readonly string baseUrl = "https://api-sandbox.pinterest.com/v5"; // Use sandbox for testing; change to "https://api.pinterest.com/v5" for production

        public PinterestHelper()
        {
            accessToken = ConfigurationManager.AppSettings["PinterestAccessToken"] ?? "";
            boardId = ConfigurationManager.AppSettings["PinterestBoardId"] ?? "";

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(boardId))
            {
                throw new InvalidOperationException("Pinterest access token or board ID not configured in App.config.");
            }
        }

        public async Task<string> CreatePinAsync(string imageUrl, PatternInfo patternInfo)
        {
            var pinData = new
            {
                board_id = boardId,
                title = patternInfo.Title ?? "Default Title", // Ensure non-null
                description = $"{patternInfo.Description ?? string.Empty}\nNotes: {patternInfo.Notes ?? string.Empty}\nDimensions: {patternInfo.Width}x{patternInfo.Height}, Colors: {patternInfo.NColors}",
                media_source = new
                {
                    source_type = "image_url",
                    url = imageUrl
                }
            };

            var response = await SendRequestAsync(HttpMethod.Post, "/pins", pinData);

            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    return "created (no ID returned in response body)";
                }

                try
                {
                    PinResponse pinResponse = JsonConvert.DeserializeObject<PinResponse>(responseContent);
                    return pinResponse.id ?? "created (ID not found in response)";
                }
                catch (JsonException ex)
                {
                    throw new Exception($"Deserialization failed: {ex.Message}. Response content: {responseContent}");
                }
            }
            else
            {
                throw new Exception($"Pinterest API error: {response.StatusCode} - {responseContent}");
            }
        }

        private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, object data)
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var request = new HttpRequestMessage(method, $"{baseUrl}{path}");

                if (data != null)
                {
                    request.Content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                }

                return await httpClient.SendAsync(request);
            }
        }

        private class PinResponse
        {
            public string id { get; set; }
        }
    }
}
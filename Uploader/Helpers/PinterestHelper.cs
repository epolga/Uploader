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
using UploadPatterns;
using System.Net;
using System.Diagnostics;
using Newtonsoft.Json.Linq; // PatternInfo

namespace Uploader.Helpers
{
    public class PinterestHelper
    {   
        private readonly string boardId;
        private string AccessToken { get; set; }
        // Production Pinterest API base URL
        private readonly string baseUrl = "https://api.pinterest.com/v5";

        public PinterestHelper()
        {
            AccessToken = ConfigurationManager.AppSettings["PinterestAccessToken"] ?? string.Empty;
            boardId = ConfigurationManager.AppSettings["PinterestBoardId"] ?? string.Empty;

            if (string.IsNullOrEmpty(AccessToken) || string.IsNullOrEmpty(boardId))
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
                    new AuthenticationHeaderValue("Bearer", AccessToken);
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

        private async Task<string> GetAccessToken()
        {
            string strAccessToken = "";
            try
            {
                string strRedirectUri = ConfigurationManager.AppSettings["PinterestRedirectUri"] ?? "http://localhost:8080/callback";
                string strAuthUrl = ConfigurationManager.AppSettings["PinterestAuthUrl"] ?? "https://www.pinterest.com/oauth/";
                string strClientId = ConfigurationManager.AppSettings["PinterestClientId"] ?? "";
                string strClientSecret = ConfigurationManager.AppSettings["PinterestClientSecret"] ?? "";
                string strScope = ConfigurationManager.AppSettings["PinterestScope"] ?? "pins:write";
                string strTokenUrl = ConfigurationManager.AppSettings["PinterestTokenUrl"] ?? "https://api.pinterest.com/v5/oauth/token";

                // Step 1: Start local HTTP listener for redirect
                var listener = new HttpListener();
                listener.Prefixes.Add(strRedirectUri.Replace("callback", "")); // Listen on http://localhost:8080/
                listener.Start();

                // Step 2: Construct authorization URL and open in default browser for user consent
                var state = Guid.NewGuid().ToString(); // CSRF protection token
                var authRequestUrl = $"{strAuthUrl}?response_type=code&client_id={strClientId}&redirect_uri={strRedirectUri}&scope={strScope}&state={state}";
                Process.Start(new ProcessStartInfo(authRequestUrl) { UseShellExecute = true });

                // Step 3: Await redirect and extract authorization code
                var context = await listener.GetContextAsync();
                var request = context.Request;
                var response = context.Response;

                var code = request.QueryString["code"];
                var returnedState = request.QueryString["state"];

                if (string.IsNullOrEmpty(code) || returnedState != state)
                {
                    throw new Exception("Invalid authorization response.");
                }

                // Send success message to browser and close listener
                var buffer = Encoding.UTF8.GetBytes("Authorization successful. You may close this window.");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                listener.Stop();


                // Step 4: Exchange authorization code for access token
                using (var httpClient = new HttpClient())
                {
                    var tokenRequest = new HttpRequestMessage(HttpMethod.Post, strTokenUrl);
                    tokenRequest.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{strClientId}:{strClientSecret}")));
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "authorization_code"),
                        new KeyValuePair<string, string>("code", code),
                        new KeyValuePair<string, string>("redirect_uri", strRedirectUri)
                    });
                    tokenRequest.Content = content;

                    var tokenResponse = await httpClient.SendAsync(tokenRequest);
                    tokenResponse.EnsureSuccessStatusCode();

                    var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                    var tokenData = JObject.Parse(tokenJson);
                    strAccessToken = tokenData["access_token"].ToString();
                }

            }
            catch
            {

            }
            return strAccessToken;
        }

        public async void UploadPin()
        {

            try
            {
                 string strAccessToken = await GetAccessToken();

                // Step 5: Create pin using public S3 image URL
                string publicImageUrl = "https://cross-stitch-designs.s3.us-east-1.amazonaws.com/images/articles/cross-stitch-completed.jpg"; // Replace with actual S3 URL
                await CreatePinAsync(strAccessToken, "https://www.cross-stitch-pattern.net", "Good Morning", "Good Morning", publicImageUrl, "257127528664615140");

            }
            catch (Exception ex)
            {
            }

        }

        private async Task CreatePinAsync(string accessToken, string link, string title, string description, string imageUrl, string boardId)
        {
            using (var httpClient = new HttpClient())
            {
                // Set authorization header with Bearer token
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

                // Construct JSON payload for pin creation
                var pinContent = new StringContent(JsonConvert.SerializeObject(new
                {
                    board_id = boardId,
                    link,
                    title,
                    description,
                    media_source = new
                    {
                        source_type = "image_url",
                        url = imageUrl
                    }
                }), Encoding.UTF8, "application/json");
                string strPinsUrl = ConfigurationManager.AppSettings["PinterestPinsUrl"] ?? "https://api.pinterest.com/v5/pins";

                // Send POST request to create the pin
                var pinResponse = await httpClient.PostAsync(strPinsUrl, pinContent);

                if (!pinResponse.IsSuccessStatusCode)
                {
                    var errorContent = await pinResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Pin creation failed: {pinResponse.StatusCode} - {errorContent}");
                }

                // Optional: Parse response to extract pin ID (for verification or logging)
                var pinJson = await pinResponse.Content.ReadAsStringAsync();
                var pinData = JObject.Parse(pinJson);
                // Example: string pinId = pinData["id"].ToString();
            }
        }


        private class PinResponse
        {
            public string id { get; set; }
        }
    }
}

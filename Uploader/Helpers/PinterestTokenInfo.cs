using System;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Uploader.Helpers
{
    /// <summary>
    /// Model for Pinterest OAuth tokens (what we store on disk).
    /// </summary>
    public class PinterestTokenInfo
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonProperty("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonProperty("token_type")]
        public string TokenType { get; set; } = "bearer";

        /// <summary>
        /// When the access token expires (UTC).
        /// </summary>
        [JsonProperty("expires_at_utc")]
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>
        /// Convenience property to check if token is expired (with small safety margin).
        /// </summary>
        [JsonIgnore]
        public bool IsExpired =>
            string.IsNullOrEmpty(AccessToken) ||
            DateTime.UtcNow >= ExpiresAtUtc.AddSeconds(-60); // 60s safety margin

        /// <summary>
        /// Build token info from the /oauth/token response (which has expires_in in seconds).
        /// </summary>
        public static PinterestTokenInfo FromResponse(PinterestTokenResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));

            return new PinterestTokenInfo
            {
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken,
                Scope = response.Scope,
                TokenType = response.TokenType,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(response.ExpiresIn)
            };
        }
    }

    /// <summary>
    /// Raw Pinterest /oauth/token response model.
    /// </summary>
    public class PinterestTokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonProperty("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonProperty("token_type")]
        public string TokenType { get; set; } = "bearer";

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }
    }

    /// <summary>
    /// Simple JSON file-based storage for Pinterest tokens.
    /// </summary>
    public class PinterestTokenStore
    {
        private readonly string _filePath;

        public PinterestTokenStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Token store path must not be empty.", nameof(filePath));

            _filePath = filePath;
        }

        public async Task<PinterestTokenInfo?> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                return JsonConvert.DeserializeObject<PinterestTokenInfo>(json);
            }
            catch
            {
                // If something goes wrong with the file, treat as no token.
                return null;
            }
        }

        public async Task SaveAsync(PinterestTokenInfo tokenInfo)
        {
            if (tokenInfo == null) throw new ArgumentNullException(nameof(tokenInfo));

            var json = JsonConvert.SerializeObject(tokenInfo, Formatting.Indented);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles OAuth code exchange, token refresh and provides valid access tokens.
    /// </summary>
    public class PinterestOAuthClient
    {
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _redirectUri;
        private readonly PinterestTokenStore _tokenStore;

        // Shared HttpClient for the whole process.
        private static readonly HttpClient HttpClient = new HttpClient();

        private const string TokenEndpoint = "https://api.pinterest.com/v5/oauth/token";

        public PinterestOAuthClient()
        {
            _clientId = ConfigurationManager.AppSettings["PinterestClientId"] ?? string.Empty;
            _clientSecret = ConfigurationManager.AppSettings["PinterestClientSecret"] ?? string.Empty;
            _redirectUri = ConfigurationManager.AppSettings["PinterestRedirectUri"] ?? string.Empty;

            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                throw new InvalidOperationException("PinterestClientId or PinterestClientSecret is not configured in App.config.");
            }

            var tokenStorePath = ConfigurationManager.AppSettings["PinterestTokenStorePath"];
            if (string.IsNullOrEmpty(tokenStorePath))
            {
                // Default to local file in current directory
                tokenStorePath = "pinterest_tokens.json";
            }

            _tokenStore = new PinterestTokenStore(tokenStorePath);
        }

        /// <summary>
        /// Exchange authorization code for access + refresh tokens (call once after user approves).
        /// </summary>
        public async Task<PinterestTokenInfo> ExchangeAuthorizationCodeAsync(string authorizationCode)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
                throw new ArgumentException("Authorization code must not be empty.", nameof(authorizationCode));

            var content = new StringContent(
                $"grant_type=authorization_code&code={Uri.EscapeDataString(authorizationCode)}" +
                $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}" +
                $"&client_id={Uri.EscapeDataString(_clientId)}" +
                $"&client_secret={Uri.EscapeDataString(_clientSecret)}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await HttpClient.PostAsync(TokenEndpoint, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Pinterest OAuth token exchange failed: {response.StatusCode} - {body}");

            var tokenResponse = JsonConvert.DeserializeObject<PinterestTokenResponse>(body)
                                ?? throw new Exception("Failed to deserialize Pinterest token response.");

            var tokenInfo = PinterestTokenInfo.FromResponse(tokenResponse);
            await _tokenStore.SaveAsync(tokenInfo).ConfigureAwait(false);

            return tokenInfo;
        }

        /// <summary>
        /// Refresh access token using stored refresh token.
        /// </summary>
        public async Task<PinterestTokenInfo> RefreshAccessTokenAsync()
        {
            var existing = await _tokenStore.LoadAsync().ConfigureAwait(false);
            if (existing == null || string.IsNullOrEmpty(existing.RefreshToken))
                throw new InvalidOperationException("No refresh token available. You must call ExchangeAuthorizationCodeAsync first.");

            // Basic auth header: base64(client_id:client_secret)
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));

            var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            // Body: grant_type + refresh_token
            var form = new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", existing.RefreshToken),
                // Optional: scope
                // new KeyValuePair<string, string>("scope", "boards:read,pins:read,boards:write,pins:write,user_accounts:read"),
            };

            request.Content = new FormUrlEncodedContent(form);

            var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Pinterest OAuth token refresh failed: {response.StatusCode} - {body}");

            var tokenResponse = JsonConvert.DeserializeObject<PinterestTokenResponse>(body)
                                ?? throw new Exception("Failed to deserialize Pinterest token refresh response.");

            // If Pinterest doesn’t send a new refresh_token, keep the old one.
            if (string.IsNullOrEmpty(tokenResponse.RefreshToken))
                tokenResponse.RefreshToken = existing.RefreshToken;

            var tokenInfo = PinterestTokenInfo.FromResponse(tokenResponse);
            await _tokenStore.SaveAsync(tokenInfo).ConfigureAwait(false);

            return tokenInfo;
        }

        /// <summary>
        /// Returns a valid access token; refreshes if needed.
        /// </summary>
        public async Task<string> GetValidAccessTokenAsync()
        {
            var tokenInfo = await _tokenStore.LoadAsync().ConfigureAwait(false);

            if (tokenInfo == null || tokenInfo.IsExpired)
                tokenInfo = await RefreshAccessTokenAsync().ConfigureAwait(false);

            return tokenInfo.AccessToken;
        }

        /// <summary>
        /// Helper for creating HttpClient with Authorization header set.
        /// </summary>
        public async Task<HttpClient> CreateAuthorizedHttpClientAsync()
        {
            var accessToken = await GetValidAccessTokenAsync().ConfigureAwait(false);

            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            return client;
        }
    }
}

using System;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Uploader.Helpers
{
    /// <summary>
    /// Example helper for uploading a digital listing to Etsy via v3 API.
    /// NOTE: All credentials are placeholders; replace with real OAuth flow and values.
    /// </summary>
    public static class EtsyHelper
    {
        private static readonly HttpClient Client = new HttpClient();
        private const string BaseUrl = "https://api.etsy.com/v3/application/";

        public static async Task UploadToEtsy()
        {
            // Replace with your actual credentials and details
            string shopId = "{your_shop_id}";
            string apiKey = "{your_api_key}";
            string bearerToken = "{your_bearer_token}";
            string filePath = @"C:\path\to\your\design.pdf";
            string title = "Cross-Stitch Pattern: Whimsical Cat Design";
            string description = "A detailed counted cross-stitch pattern featuring a playful cat.";
            decimal price = 5.00m; // USD
            string whoMade = "i_did";
            string whenMade = "made_to_order";
            string taxonomyId = "1234"; // Replace with real taxonomy ID
            string imagePath = @"C:\path\to\your\design_image.jpg";

            // 1. Create draft listing
            string? listingId = await CreateDraftListing(
                shopId, apiKey, bearerToken, title, description, price, whoMade, whenMade, taxonomyId);

            if (string.IsNullOrEmpty(listingId))
            {
                Console.WriteLine("Failed to create listing.");
                return;
            }

            Console.WriteLine($"Draft listing created with ID: {listingId}");

            // 2. Upload listing image
            bool imageUploaded = await UploadListingImage(shopId, listingId, apiKey, bearerToken, imagePath);
            if (!imageUploaded)
            {
                Console.WriteLine("Failed to upload listing image.");
                return;
            }

            Console.WriteLine("Listing image uploaded successfully.");

            // 3. Upload digital file
            bool fileUploaded = await UploadDigitalFile(shopId, listingId, apiKey, bearerToken, filePath);
            if (!fileUploaded)
            {
                Console.WriteLine("Failed to upload file.");
                return;
            }

            Console.WriteLine("Digital file uploaded successfully.");

            // 4. Update listing type to "download" (digital product)
            bool typeUpdated = await UpdateListingType(shopId, listingId, apiKey, bearerToken, "download");
            if (!typeUpdated)
            {
                Console.WriteLine("Failed to update listing type.");
                return;
            }

            Console.WriteLine("Listing type updated to digital download.");

            // Optional: activate listing if you want it live
            // bool activated = await ActivateListing(shopId, listingId, apiKey, bearerToken);
            // Console.WriteLine(activated ? "Listing activated." : "Failed to activate listing.");
        }

        private static async Task<string?> CreateDraftListing(
            string shopId,
            string apiKey,
            string bearerToken,
            string title,
            string description,
            decimal price,
            string whoMade,
            string whenMade,
            string taxonomyId)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings";

            var form = new NameValueCollection
            {
                { "quantity", "999" },
                { "title", title },
                { "description", description },
                { "price", ((int)(price * 100)).ToString() }, // cents
                { "who_made", whoMade },
                { "when_made", whenMade },
                { "taxonomy_id", taxonomyId }
            };

            var content = new StringContent(BuildFormData(form), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            if (!response.IsSuccessStatusCode)
                return null;

            string result = await response.Content.ReadAsStringAsync();
            // TODO: parse result JSON to extract listing_id.
            return ExtractListingId(result);
        }

        private static async Task<bool> UploadListingImage(
            string shopId,
            string listingId,
            string apiKey,
            string bearerToken,
            string imagePath)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}/images";

            var content = new MultipartFormDataContent();
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            content.Add(new ByteArrayContent(imageBytes), "image", Path.GetFileName(imagePath));

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> UploadDigitalFile(
            string shopId,
            string listingId,
            string apiKey,
            string bearerToken,
            string filePath)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}/files";

            var content = new MultipartFormDataContent();
            byte[] fileBytes = File.ReadAllBytes(filePath);
            content.Add(new ByteArrayContent(fileBytes), "file", Path.GetFileName(filePath));

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> UpdateListingType(
            string shopId,
            string listingId,
            string apiKey,
            string bearerToken,
            string type)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}";

            var form = new NameValueCollection { { "type", type } };
            var content = new StringContent(BuildFormData(form), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, new HttpMethod("PATCH"), apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> ActivateListing(
            string shopId,
            string listingId,
            string apiKey,
            string bearerToken)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}";
            var form = new NameValueCollection { { "state", "active" } };
            var content = new StringContent(BuildFormData(form), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, new HttpMethod("PATCH"), apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<HttpResponseMessage> SendRequestAsync(
            string url,
            HttpMethod method,
            string apiKey,
            string bearerToken,
            HttpContent content)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Content = content;

            return await Client.SendAsync(request).ConfigureAwait(false);
        }

        private static string BuildFormData(NameValueCollection collection)
        {
            var sb = new StringBuilder();

            foreach (string key in collection.Keys)
            {
                if (key == null) continue;
                string value = collection[key] ?? string.Empty;
                sb.Append($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}&");
            }

            return sb.ToString().TrimEnd('&');
        }

        private static string ExtractListingId(string jsonResponse)
        {
            // TODO: Implement JSON parsing to extract "listing_id" from response.
            // This is a placeholder to keep the same behavior.
            return "extracted_listing_id";
        }
    }
}

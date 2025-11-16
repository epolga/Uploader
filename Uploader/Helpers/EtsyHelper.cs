using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Uploader.Helpers
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading.Tasks;
    using System.Collections.Specialized;

    class EtsyHelper
    {
        private static readonly HttpClient client = new HttpClient();
        private const string BaseUrl = "https://api.etsy.com/v3/application/";

        public static async Task UploadToEtsy()
        {
            // Replace with your actual credentials and details
            string shopId = "{your_shop_id}"; // e.g., "12345678"
            string apiKey = "{your_api_key}"; // e.g., "1aa2bb33c44d55eeeeee6fff"
            string bearerToken = "{your_bearer_token}"; // e.g., "12345678.jKBPLnOiYt7vpWlsny_lDKqINn4Ny_jwH89hA4IZgggyzqmV_bmQHGJ3HOHH2DmZxOJn5V1qQFnVP9bCn9jnrggCRz"
            string filePath = @"C:\path\to\your\design.pdf"; // Path to the digital file (e.g., PDF pattern)
            string title = "Cross-Stitch Pattern: Whimsical Cat Design";
            string description = "A detailed counted cross-stitch pattern featuring a playful cat, suitable for beginners and experts.";
            decimal price = 5.00m; // Price in USD; will be converted to cents
            string whoMade = "i_did";
            string whenMade = "made_to_order";
            string taxonomyId = "1234"; // Replace with actual taxonomy ID for cross-stitch patterns (e.g., from Etsy's seller taxonomy)
            string imagePath = @"C:\path\to\your\design_image.jpg";

            // Step 1: Create a draft listing
            string listingId = await CreateDraftListing(shopId, apiKey, bearerToken, title, description, price, whoMade, whenMade, taxonomyId);
            if (string.IsNullOrEmpty(listingId))
            {
                Console.WriteLine("Failed to create listing.");
                return;
            }
            Console.WriteLine($"Draft listing created with ID: {listingId}");

            // Step 2: Upload the listing image (picture of the design)bool imageUploaded = await UploadListingImage(shopId, listingId, apiKey, bearerToken, imagePath);
            bool imageUploaded = await UploadListingImage(shopId, listingId, apiKey, bearerToken, imagePath); if (!imageUploaded)
            {
                Console.WriteLine("Failed to upload listing image.");
                return;
            }
            Console.WriteLine("Listing image uploaded successfully.");

            // Step 3: Upload the digital file
            bool fileUploaded = await UploadDigitalFile(shopId, listingId, apiKey, bearerToken, filePath);
            if (!fileUploaded)
            {
                Console.WriteLine("Failed to upload file.");
                return;
            }
            Console.WriteLine("Digital file uploaded successfully.");

            // Step 4: Update listing type to "download" for digital products
            bool typeUpdated = await UpdateListingType(shopId, listingId, apiKey, bearerToken, "download");
            if (!typeUpdated)
            {
                Console.WriteLine("Failed to update listing type.");
                return;
            }
            Console.WriteLine("Listing type updated to digital download.");

            // Optional: Activate the listing (requires at least one image uploaded separately if publishing)
            // bool activated = await ActivateListing(shopId, listingId, apiKey, bearerToken);
            // Console.WriteLine(activated ? "Listing activated." : "Failed to activate listing.");
        }

        private static async Task<string> CreateDraftListing(string shopId, string apiKey, string bearerToken,
            string title, string description, decimal price, string whoMade, string whenMade, string taxonomyId)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings";
            var content = new StringContent(BuildFormData(new NameValueCollection
        {
            { "quantity", "999" }, // High quantity for digital items
            { "title", title },
            { "description", description },
            { "price", ((int)(price * 100)).ToString() }, // Convert to cents
            { "who_made", whoMade },
            { "when_made", whenMade },
            { "taxonomy_id", taxonomyId },
            // Add other optional params as needed, e.g., "tags" as comma-separated string
        }), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            if (response.IsSuccessStatusCode)
            {
                string result = await response.Content.ReadAsStringAsync();
                // Parse JSON to extract listing_id (e.g., using System.Text.Json)
                // For simplicity, assume extraction logic here; in production, use JsonDocument
                // Example: var json = JsonDocument.Parse(result); string listingId = json.RootElement.GetProperty("listing_id").GetString();
                return ExtractListingId(result); // Implement extraction as needed
            }
            return null;
        }

        private static async Task<bool> UploadListingImage(string shopId, string listingId, string apiKey, string bearerToken, string imagePath)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}/images";
            var content = new MultipartFormDataContent();
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            content.Add(new ByteArrayContent(imageBytes), "image", Path.GetFileName(imagePath));
            // Optional parameters (uncomment and adjust as needed):
            // content.Add(new StringContent("1"), "rank"); // Position of the image (1-10)
            // content.Add(new StringContent("true"), "overwrite"); // Overwrite existing image at rank
            // content.Add(new StringContent("false"), "is_watermarked"); // Apply watermark
            // content.Add(new StringContent("Preview of whimsical cat cross-stitch pattern"), "alt_text"); // Accessibility text

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> UploadDigitalFile(string shopId, string listingId, string apiKey, string bearerToken, string filePath)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}/files";
            var content = new MultipartFormDataContent();
            byte[] fileBytes = File.ReadAllBytes(filePath);
            content.Add(new ByteArrayContent(fileBytes), "file", Path.GetFileName(filePath));

            HttpResponseMessage response = await SendRequestAsync(url, HttpMethod.Post, apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> UpdateListingType(string shopId, string listingId, string apiKey, string bearerToken, string type)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}";
            var content = new StringContent(BuildFormData(new NameValueCollection { { "type", type } }), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, new HttpMethod("PATCH"), apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> ActivateListing(string shopId, string listingId, string apiKey, string bearerToken)
        {
            string url = $"{BaseUrl}shops/{shopId}/listings/{listingId}";
            var content = new StringContent(BuildFormData(new NameValueCollection { { "state", "active" } }), Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await SendRequestAsync(url, new HttpMethod("PATCH"), apiKey, bearerToken, content);
            return response.IsSuccessStatusCode;
        }

        private static async Task<HttpResponseMessage> SendRequestAsync(string url, HttpMethod method, string apiKey, string bearerToken, HttpContent content)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Content = content;
            return await client.SendAsync(request);
        }

        private static string BuildFormData(NameValueCollection collection)
        {
            var sb = new StringBuilder();
            foreach (string key in collection.Keys)
            {
                sb.Append($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(collection[key])}&");
            }
            return sb.ToString().TrimEnd('&');
        }

        private static string ExtractListingId(string jsonResponse)
        {
            // Implement JSON parsing to extract "listing_id" from response
            // Use System.Text.Json or Newtonsoft.Json in production
            // Placeholder: return the ID as string
            return "extracted_listing_id"; // Replace with actual parsing
        }
    }
}

/*
 Authentication: All requests include the x-api-key header and Authorization: Bearer header. Obtain the bearer token via Etsy's OAuth 2.0 flow (not shown in code, as it requires user authorization).
Creating the Listing: Uses POST to /v3/application/shops/{shop_id}/listings with form-urlencoded parameters. The listing starts as a draft.
Uploading the File: Uses POST to /v3/application/shops/{shop_id}/listings/{listing_id}/files with multipart/form-data for binary file handling, ensuring reliable upload of PDFs or images.
Updating Type: Uses PATCH to set the type to "download" for digital items.
Activation: Optional PATCH to set state to "active" (uncomment if needed; requires an image for published listings).
Error Handling: Basic success checks are included; enhance with try-catch and detailed logging in production.
Dependencies: Requires .NET with System.Net.Http. For JSON parsing (e.g., extracting listing_id), add System.Text.Json or a similar library.
s*/
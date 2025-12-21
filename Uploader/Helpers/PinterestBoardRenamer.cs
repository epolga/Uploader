using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Uploader.Helpers
{
    /// <summary>
    /// Reads AlbumBoards.csv (AlbumID,Caption,BoardID) and renames boards on Pinterest
    /// to more SEO friendly names.
    /// </summary>
    public class PinterestBoardRenamer
    {
        private readonly string _csvPath;
        private readonly PinterestOAuthClient _pinterestOAuthClient;

        public PinterestBoardRenamer()
        {
            _csvPath = ConfigurationManager.AppSettings["PinterestBoardsCsvPath"] ?? "AlbumBoards.csv";
            _pinterestOAuthClient = new PinterestOAuthClient();
        }

        /// <summary>
        /// Main entry: read CSV, compute new names, send PATCH requests to Pinterest.
        /// </summary>
        public async Task RenameBoardsFromCsvAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_csvPath))
            {
                throw new FileNotFoundException("CSV file with board mapping not found.", _csvPath);
            }

            progress?.Report($"Reading CSV: {_csvPath}");

            var lines = await File.ReadAllLinesAsync(_csvPath, Encoding.UTF8).ConfigureAwait(false);
            if (lines.Length <= 1)
            {
                progress?.Report("CSV file contains no data lines.");
                return;
            }

            // Obtain valid access token (includes refresh if needed)
            string accessToken = await _pinterestOAuthClient.GetValidAccessTokenAsync().ConfigureAwait(false);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            // First line is header: AlbumID,AlbumCaption,BoardID
            for (int i = 1; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseCsvLine(line, out string albumId, out string caption, out string boardId))
                {
                    progress?.Report($"Skipping invalid CSV line {i + 1}: {line}");
                    continue;
                }

                string newName = BuildSeoBoardName(caption);
                string newDescription = BuildSeoBoardDescription(albumId, caption);
                 
                progress?.Report(
                    $"Renaming board {boardId}: '{caption}' => '{newName}'");
                  await RenameBoardAsync(httpClient, boardId, newName, newDescription, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report("Board renaming completed.");
        }

        /// <summary>
        /// Parses a CSV line of the form: AlbumID,"Caption",BoardID.
        /// Supports quotes in caption (escaped as "").
        /// </summary>
        private static bool TryParseCsvLine(string line, out string albumId, out string caption, out string boardId)
        {
            albumId = string.Empty;
            caption = string.Empty;
            boardId = string.Empty;

            // Expect at least: a,b,c
            int firstComma = line.IndexOf(',');
            if (firstComma < 0) return false;

            albumId = line[..firstComma].Trim();

            // Caption is quoted
            int firstQuote = line.IndexOf('"', firstComma + 1);
            if (firstQuote < 0) return false;

            int closingQuote = FindClosingQuote(line, firstQuote + 1);
            if (closingQuote < 0) return false;

            caption = line.Substring(firstQuote + 1, closingQuote - firstQuote - 1)
                          .Replace("\"\"", "\""); // unescape double quotes

            int commaAfterCaption = line.IndexOf(',', closingQuote + 1);
            if (commaAfterCaption < 0) return false;

            boardId = line[(commaAfterCaption + 1)..].Trim();

            return !string.IsNullOrEmpty(albumId) &&
                   !string.IsNullOrEmpty(boardId);
        }

        /// <summary>
        /// Finds the closing quote in CSV, skipping escaped double quotes ("").
        /// </summary>
        private static int FindClosingQuote(string line, int startIndex)
        {
            int i = startIndex;
            while (i < line.Length)
            {
                int quoteIndex = line.IndexOf('"', i);
                if (quoteIndex < 0)
                    return -1;

                // Escaped quote: ""
                if (quoteIndex + 1 < line.Length && line[quoteIndex + 1] == '"')
                {
                    i = quoteIndex + 2; // skip both quotes
                    continue;
                }

                return quoteIndex;
            }

            return -1;
        }

        /// <summary>
        /// Builds a more SEO-friendly board name based on album caption.
        /// Examples:
        ///   "Cute Cats" => "Cute Cats Cross Stitch Patterns"
        ///   ""          => "Cross Stitch Patterns"
        /// </summary>
        private static string BuildSeoBoardName(string caption)
        {
            string baseCaption = (caption ?? string.Empty).Trim();

            // Fallback if caption is empty
            if (baseCaption.Length == 0)
                baseCaption = "Cross Stitch Free";

            // Always add main keyword, but keep very short
            string result;

            if (baseCaption.IndexOf("Cross Stitch Free", StringComparison.OrdinalIgnoreCase) < 0)
            {
                result = $"{baseCaption} Cross Stitch Free";
            }
            else
            {
                result = baseCaption;
            }

            // MAX LENGTH MUST BE < 50 (Pinterest requirement)
            const int maxLength = 48; // leave space for safety
            if (result.Length > maxLength)
            {
                // Try trimming words instead of hard cutting mid-word
                result = TrimToMaxLength(result, maxLength);
            }

            return result;
        }

        /// <summary>
        /// Trims string to word boundary under max length.
        /// </summary>
        private static string TrimToMaxLength(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;

            // Find last space before limit
            int lastSpace = text.LastIndexOf(' ', maxLength);
            if (lastSpace > 0)
                return text.Substring(0, lastSpace);

            return text.Substring(0, maxLength);
        }

        /// <summary>
        /// Builds board description with more SEO context and site name.
        /// </summary>
        private static string BuildSeoBoardDescription(string albumId, string caption)
        {
            string safeCaption = (caption ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(safeCaption))
            {
                safeCaption = "beautiful counted cross stitch designs";
            }

            string description =
                $"Cross stitch patterns from album {safeCaption}. " +
                "Discover printable cross stitch charts and downloadable PDF patterns at Cross-Stitch.com . " +
                "Perfect for both beginners and experienced stitchers who love detailed embroidery designs.";

            // Keep it reasonably short (Pinterest allows long descriptions but no need to overdo it).
            const int maxLength = 500;
            if (description.Length > maxLength)
            {
                description = description.Substring(0, maxLength);
            }

            return description;
        }

        /// <summary>
        /// Sends PATCH request to Pinterest API to rename a board and update description.
        /// </summary>
        private static async Task RenameBoardAsync(
            HttpClient httpClient,
            string boardId,
            string newName,
            string newDescription,
            CancellationToken cancellationToken)
        {
            string url = $"https://api.pinterest.com/v5/boards/{boardId}";

            var payload = new
            {
                name = newName,
                description = newDescription
            };

            string json = JsonConvert.SerializeObject(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to rename board {boardId}: {response.StatusCode} - {body}");
            }
        }
    }
}

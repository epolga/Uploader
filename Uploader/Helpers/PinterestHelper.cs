// Uploader/Helpers/PinterestHelper.cs
// Pinterest helper: automatic theme detection, SEO text, hashtags,
// and board selection by AlbumID from AlbumBoards.csv.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UploadPatterns;

namespace Uploader.Helpers
{
    /// <summary>
    /// Helper for creating Pinterest Pins for cross-stitch designs.
    /// Features:
    /// - Uses Pinterest API v5 (`https://api.pinterest.com/v5/pins`).
    /// - Uses OAuth access token via PinterestTokenInfo.
    /// - Picks board by AlbumID using AlbumBoards.csv.
    /// - Automatically detects theme (cats, dogs, flowers, nature, city, people, fantasy, Christmas, etc.).
    /// - Generates SEO-friendly title, description, alt text and hashtags.
    /// </summary>
    public class PinterestHelper
    {
        private const string PinterestApiBaseUrl = "https://api.pinterest.com/v5";

        private readonly string _siteBaseUrl;
        private readonly string _imageBaseUrl;
        private readonly string _boardsCsvPath;
        private readonly string? _defaultBoardId;

        // Lazy cache: AlbumID (4-digit string) -> BoardID
        private Dictionary<string, string>? _boardIdByAlbumId;

        private readonly PinterestTokenInfo _tokenStore = new PinterestTokenInfo();
        private readonly PinterestOAuthClient _pinterestOAuthClient = new PinterestOAuthClient();
        public PinterestHelper()
        {
            // Base URL of your site, used for the "link" field of the Pin.
            // Example: https://www.cross-stitch-pattern.net
            _siteBaseUrl =
                ConfigurationManager.AppSettings["SiteBaseUrl"] ??
                "https://www.cross-stitch-pattern.net";

            // Public S3 base URL for images.
            // Example: https://cross-stitch-designs.s3.amazonaws.com
            // If not provided, build from S3 bucket name or fall back to a default.
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

            // CSV with mapping AlbumID,AlbumCaption,BoardID
            // Created previously by PinterestBoardCreator.
            _boardsCsvPath =
                ConfigurationManager.AppSettings["PinterestBoardsCsvPath"]
                ?? "AlbumBoards.csv";

            // Fallback board, used if album is not found in CSV.
            _defaultBoardId = ConfigurationManager.AppSettings["PinterestBoardId"];
        }

        #region Public API

        /// <summary>
        /// Create a Pinterest Pin for a design.
        /// </summary>
        /// <param name="pattern">Cross-stitch pattern metadata.</param>
        /// <param name="albumId">Numeric AlbumID of this design.</param>
        /// <returns>Created Pin ID.</returns>
        public async Task<string> UploadPinForPatternAsync(PatternInfo pattern, bool test = false)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (test)
            {
                pattern.DesignID = 5375;
                pattern.AlbumId = 18;
                pattern.NPage = "00167";
            }
            if (pattern.DesignID <= 0)
                throw new ArgumentException("DesignID must be set before uploading a pin.", nameof(pattern));

            // 1. Resolve boardId via CSV or default
            string boardId = test ? "257127528664615140" // Test board ID
                :
                await GetBoardIdForAlbumAsync(pattern.AlbumId).ConfigureAwait(false);

            // 2. Build URLs
            string patternUrl = BuildPatternUrl(pattern);
            string imageUrl = BuildImageUrl(pattern.DesignID, pattern.AlbumId);

            // 3. Analyze theme and build SEO text
            var theme = DetectTheme(pattern);
            string title = BuildPinTitle(pattern, theme);
            string description = BuildPinDescription(pattern, theme, pattern.AlbumId, patternUrl);
            string altText = BuildAltText(pattern, theme);

            // 4. Execute HTTP request
            string accessToken = await _pinterestOAuthClient.GetValidAccessTokenAsync().ConfigureAwait(false);
            var handler = new HttpClientHandler
            {
                AutomaticDecompression =
                 DecompressionMethods.GZip |
                 DecompressionMethods.Deflate |
                 DecompressionMethods.Brotli
            };
            
            using var client = new HttpClient(handler);
           
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var payload = new
            {
                board_id = boardId,
                link = patternUrl,
                title,
                description,
                alt_text = altText,
                media_source = new
                {
                    source_type = "image_url",
                    url = imageUrl
                }
            };

            string json = JsonConvert.SerializeObject(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{PinterestApiBaseUrl}/pins")
            {
                Content = content
            };

             var response = await client.SendAsync(request).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Failed to create Pin. Status: {response.StatusCode}, Body: {responseBody}");
            }

            var pinResponse = JsonConvert.DeserializeObject<PinResponse>(responseBody);
            if (pinResponse == null || string.IsNullOrWhiteSpace(pinResponse.id))
            {
                throw new Exception($"Pinterest Pin created but response did not contain an id. Body: {responseBody}");
            }

            return pinResponse.id;
        }

        #endregion

        #region Board mapping (AlbumBoards.csv)

        /// <summary>
        /// Get board ID for the given AlbumID (int). Looks in AlbumBoards.csv.
        /// If not found, returns _defaultBoardId or throws if that is missing too.
        /// </summary>
        private async Task<string> GetBoardIdForAlbumAsync(int albumId)
        {
            string albumKey = albumId.ToString("D4", CultureInfo.InvariantCulture);

            // Lazy-load CSV once
            if (_boardIdByAlbumId == null)
            {
                _boardIdByAlbumId = await LoadBoardsMappingAsync().ConfigureAwait(false);
            }

            if (_boardIdByAlbumId.TryGetValue(albumKey, out string? boardId) &&
                !string.IsNullOrWhiteSpace(boardId))
            {
                return boardId;
            }

            if (!string.IsNullOrWhiteSpace(_defaultBoardId))
            {
                return _defaultBoardId;
            }

            throw new InvalidOperationException(
                $"Board for album {albumId} not found in '{_boardsCsvPath}', " +
                "and no PinterestBoardId fallback is configured.");
        }

        /// <summary>
        /// Loads AlbumBoards.csv into a dictionary AlbumID(4-digit) -> BoardID.
        /// CSV format:
        ///   AlbumID,AlbumCaption,BoardID
        /// Caption may be quoted and contain commas.
        /// </summary>
        private async Task<Dictionary<string, string>> LoadBoardsMappingAsync()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(_boardsCsvPath))
            {
                // If CSV is missing, we return empty map and let caller fall back to default board.
                return map;
            }

            string[] lines = await File.ReadAllLinesAsync(_boardsCsvPath, Encoding.UTF8)
                                       .ConfigureAwait(false);

            if (lines.Length <= 1)
                return map; // header only

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseAlbumBoardsCsvLine(line, out string albumId, out string boardId))
                    continue;

                if (!map.ContainsKey(albumId))
                    map[albumId] = boardId;
            }

            return map;
        }

        /// <summary>
        /// Parse line: AlbumID,"Album Caption",BoardID.
        /// We only care about AlbumID and BoardID.
        /// </summary>
        private static bool TryParseAlbumBoardsCsvLine(string line, out string albumId, out string boardId)
        {
            albumId = string.Empty;
            boardId = string.Empty;

            // Very simple CSV parsing:
            //  1) take first token until first comma  => AlbumID
            //  2) find last comma                   => before BoardID
            //  3) everything after last comma       => BoardID
            int firstComma = line.IndexOf(',');
            int lastComma = line.LastIndexOf(',');

            if (firstComma <= 0 || lastComma <= firstComma)
                return false;

            albumId = line.Substring(0, firstComma).Trim();

            string boardIdRaw = line.Substring(lastComma + 1).Trim();
            if (boardIdRaw.StartsWith("\"") && boardIdRaw.EndsWith("\"") && boardIdRaw.Length >= 2)
            {
                boardIdRaw = boardIdRaw.Substring(1, boardIdRaw.Length - 2);
            }

            boardId = boardIdRaw;

            return !string.IsNullOrEmpty(albumId) && !string.IsNullOrEmpty(boardId);
        }

        #endregion

        #region URLs

        private string BuildPatternUrl(PatternInfo patternInfo)
        {
            string caption = patternInfo.Title.Replace(' ', '-');
            // Example: https://www.cross-stitch-pattern.net/Good-Morning-9-289-Free-Design.aspx
            Int32.TryParse(patternInfo.NPage, out int nPage);
            string baseUrl = _siteBaseUrl.TrimEnd('/');
            return $"{baseUrl}/{caption}-{patternInfo.AlbumId}-{nPage}-Free-Design.aspx";
        }

        private string BuildImageUrl(int designId, int albumId)
        {
            // Match the same S3 structure you use when uploading thumbnails.
            // Example: {imageBaseUrl}/images/designs/photos/{albumId}/{designId}/4.jpg
            string photoFileName = "4.jpg";
            string photoPrefix =
                ConfigurationManager.AppSettings["S3PhotoPrefix"] ??
                "images/designs/photos";

            return $"{_imageBaseUrl}/{photoPrefix}/{albumId}/{designId}/{photoFileName}";
        }

        #endregion

        #region Theme detection + SEO text

        private sealed class Theme
        {
            public string Code { get; init; } = "";
            public string HumanName { get; init; } = "";
            public string[] Keywords { get; init; } = Array.Empty<string>();
            public string[] Hashtags { get; init; } = Array.Empty<string>();
        }

        private static readonly Theme DefaultTheme = new Theme
        {
            Code = "general",
            HumanName = "cross stitch pattern",
            Keywords = Array.Empty<string>(),
            Hashtags = new[]
            {
                "#crossstitch", "#crossstitchpattern", "#embroidery", "#needlework"
            }
        };

        // You can extend or tweak this table anytime.
        private static readonly Theme[] Themes =
        {
            new Theme
            {
                Code = "cats",
                HumanName = "cat cross stitch pattern",
                Keywords = new[] { "cat", "kitten", "kitty" },
                Hashtags = new[] { "#cat", "#cats", "#catlover", "#kitty" }
            },
            new Theme
            {
                Code = "dogs",
                HumanName = "dog cross stitch pattern",
                Keywords = new[] { "dog", "puppy", "pup" },
                Hashtags = new[] { "#dog", "#dogs", "#doglover", "#puppy" }
            },
            new Theme
            {
                Code = "birds",
                HumanName = "bird cross stitch pattern",
                Keywords = new[] { "bird", "sparrow", "owl", "eagle", "parrot" },
                Hashtags = new[] { "#birds", "#birdart" }
            },
            new Theme
            {
                Code = "flowers",
                HumanName = "floral cross stitch pattern",
                Keywords = new[] { "flower", "rose", "tulip", "poppy", "bouquet", "floral" },
                Hashtags = new[] { "#flowers", "#floral" }
            },
            new Theme
            {
                Code = "nature",
                HumanName = "nature cross stitch pattern",
                Keywords = new[] { "forest", "tree", "mountain", "lake", "river", "landscape", "nature" },
                Hashtags = new[] { "#landscape", "#nature" }
            },
            new Theme
            {
                Code = "seaside",
                HumanName = "seaside cross stitch pattern",
                Keywords = new[] { "sea", "ocean", "beach", "coast", "harbor", "harbour" },
                Hashtags = new[] { "#seaside", "#ocean", "#beach" }
            },
            new Theme
            {
                Code = "city",
                HumanName = "city cross stitch pattern",
                Keywords = new[] { "city", "street", "house", "houses", "architecture", "building" },
                Hashtags = new[] { "#cityscape", "#architecture" }
            },
            new Theme
            {
                Code = "people",
                HumanName = "people cross stitch pattern",
                Keywords = new[] { "girl", "boy", "woman", "man", "people", "portrait" },
                Hashtags = new[] { "#portrait", "#people" }
            },
            new Theme
            {
                Code = "fantasy",
                HumanName = "fantasy cross stitch pattern",
                Keywords = new[] { "fairy", "dragon", "unicorn", "wizard", "magic", "fantasy" },
                Hashtags = new[] { "#fantasy", "#fairytales" }
            },
            new Theme
            {
                Code = "christmas",
                HumanName = "Christmas cross stitch pattern",
                Keywords = new[] { "christmas", "xmas", "santa", "snowman", "reindeer", "christmas tree" },
                Hashtags = new[] { "#christmas", "#christmasdecor", "#winter" }
            },
            new Theme
            {
                Code = "easter",
                HumanName = "Easter cross stitch pattern",
                Keywords = new[] { "easter", "egg", "eggs", "bunny", "rabbit" },
                Hashtags = new[] { "#easter", "#spring" }
            }
        };

        /// <summary>
        /// Pick the theme with maximum keyword matches in title + description + notes.
        /// If nothing matches, fallback to DefaultTheme.
        /// </summary>
        private static Theme DetectTheme(PatternInfo pattern)
        {
            string text =
                $"{pattern.Title} {pattern.Description} {pattern.Notes}"
                    .ToLowerInvariant();

            Theme bestTheme = DefaultTheme;
            int bestScore = 0;

            foreach (var theme in Themes)
            {
                int score = 0;
                foreach (var kw in theme.Keywords)
                {
                    if (string.IsNullOrWhiteSpace(kw))
                        continue;

                    if (text.Contains(kw.ToLowerInvariant()))
                        score++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTheme = theme;
                }
            }

            return bestTheme;
        }

        private static string BuildPinTitle(PatternInfo pattern, Theme theme)
        {
            // Something like:
            //   "Cute Cats – Cat Cross Stitch Pattern, Printable PDF"
            string? titleBase = pattern.Title?.Trim();
            if (string.IsNullOrEmpty(titleBase))
            {
                titleBase = "Cross stitch pattern";
            }

            string title =
                $"{titleBase} – {ToSentenceCase(theme.HumanName)}, printable PDF pattern";

            // Pinterest title limit ~100 chars; keep it safe.
            const int maxLength = 100;
            if (title.Length > maxLength)
            {
                title = title.Substring(0, maxLength);
            }

            return title;
        }

        private static string BuildAltText(PatternInfo pattern, Theme theme)
        {
            // Alt text is mainly for accessibility: concise and visual.
            // Example:
            //   "Counted cross stitch pattern with two cute cats, 150 by 200 stitches, 40 colours."
            string titlePart = pattern.Title;
            if (string.IsNullOrWhiteSpace(titlePart))
            {
                titlePart = theme.HumanName;
            }

            string sizePart = "";
            if (pattern.Width > 0 && pattern.Height > 0)
            {
                sizePart = $"{pattern.Width} by {pattern.Height} stitches";
            }

            string colorPart = "";
            if (pattern.NColors > 0)
            {
                colorPart = $"{pattern.NColors} colours";
            }

            var parts = new List<string>
            {
                "Counted cross stitch pattern",
                titlePart
            };

            var techParts = new List<string>();
            if (!string.IsNullOrEmpty(sizePart)) techParts.Add(sizePart);
            if (!string.IsNullOrEmpty(colorPart)) techParts.Add(colorPart);

            if (techParts.Count > 0)
            {
                parts.Add(string.Join(", ", techParts));
            }

            string alt = string.Join(", ", parts);

            // Alt text max 500 chars.
            const int maxLength = 500;
            if (alt.Length > maxLength)
            {
                alt = alt.Substring(0, maxLength);
            }

            return alt;
        }

        private static string BuildPinDescription(
            PatternInfo pattern,
            Theme theme,
            int albumId,
            string patternUrl)
        {
            // Main text with SEO keywords + hashtags.
            var sb = new StringBuilder();

            string title = pattern.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                sb.Append(title);
                sb.Append(" – ");
            }

            sb.Append(theme.HumanName);
            sb.Append(". ");

            if (pattern.Width > 0 && pattern.Height > 0 && pattern.NColors > 0)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "Detailed counted cross stitch chart ({0} × {1} stitches, {2} colours). ",
                    pattern.Width,
                    pattern.Height,
                    pattern.NColors);
            }
            else
            {
                sb.Append("Beautiful counted cross stitch design. ");
            }

            if (!string.IsNullOrWhiteSpace(pattern.Description))
            {
                sb.Append(pattern.Description.Trim());
                sb.Append(" ");
            }

            if (!string.IsNullOrWhiteSpace(pattern.Notes))
            {
                sb.Append(pattern.Notes.Trim());
                sb.Append(" ");
            }

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "From album {0:D4}. Download printable PDF and see more details at {1}. ",
                albumId,
                patternUrl);

            sb.Append("Perfect for embroidery lovers and cross stitch fans. ");

            // Generic hashtags
            var hashtags = new List<string>
            {
                "#crossstitch",
                "#crossstitchpattern",
                "#embroidery",
                "#needlework",
                "#crossstitchkit"
            };

            // Theme-specific hashtags
            hashtags.AddRange(theme.Hashtags);

            // Remove duplicates, keep order
            var uniqueHashtags = hashtags
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uniqueHashtags.Count > 0)
            {
                sb.AppendLine();
                sb.Append(string.Join(" ", uniqueHashtags));
            }

            string description = sb.ToString();

            // Pinterest description limit ~500 characters.
            const int maxLength = 500;
            if (description.Length > maxLength)
            {
                description = description.Substring(0, maxLength);
            }

            return description;
        }

        private static string ToSentenceCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            var sb = new System.Text.StringBuilder(input.Length);
            bool newSentence = true;

            foreach (char c in input)
            {
                if (newSentence && char.IsLetter(c))
                {
                    sb.Append(char.ToUpper(c));
                    newSentence = false;
                }
                else
                {
                    sb.Append(char.ToLower(c));
                }

                if (c == '.' || c == '!' || c == '?')
                    newSentence = true;
            }

            return sb.ToString();
        }

        #endregion

        #region DTOs

        private sealed class PinResponse
        {
            public string id { get; set; } = "";
        }

        #endregion
    }
}

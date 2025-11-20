using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Newtonsoft.Json;

namespace Uploader.Helpers
{
    /// <summary>
    /// Simple DTO for album information.
    /// </summary>
    public class AlbumInfo
    {
        public string AlbumId { get; set; } = string.Empty;  // e.g. "0001"
        public string Caption { get; set; } = string.Empty;  // e.g. "Cute Cats"
    }

    /// <summary>
    /// DTO for Pinterest boards API response.
    /// </summary>
    public class PinterestBoardResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Creates Pinterest Boards from DynamoDB albums
    /// and writes CSV mapping: AlbumID,Caption,BoardID.
    /// Uses PinterestOAuthClient to obtain a valid access token.
    /// </summary>
    public class PinterestBoardCreator
    {
        private readonly AmazonDynamoDBClient _dynamoDbClient;
        private readonly string _tableName;
        private readonly string _csvPath;
        private readonly PinterestOAuthClient _pinterestOAuthClient;

        public PinterestBoardCreator()
        {
            _dynamoDbClient = new AmazonDynamoDBClient(RegionEndpoint.USEast1);

            _tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";
            _csvPath = ConfigurationManager.AppSettings["PinterestBoardsCsvPath"] ?? "AlbumBoards.csv";

            _pinterestOAuthClient = new PinterestOAuthClient();
        }

        /// <summary>
        /// Main entry: loads albums, creates boards, writes CSV.
        /// </summary>
        public async Task CreateBoardsAndCsvAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report("Loading albums from DynamoDB...");

            var albums = await LoadAlbumsAsync(progress, cancellationToken).ConfigureAwait(false);
            progress?.Report($"Found {albums.Count} albums.");

            if (albums.Count == 0)
            {
                progress?.Report("No albums found with EntityType = 'ALBUM'. Nothing to do.");
                return;
            }

            // Get a valid access token (uses refresh internally)
            string accessToken = await _pinterestOAuthClient.GetValidAccessTokenAsync().ConfigureAwait(false);

            progress?.Report("Creating boards on Pinterest...");

            var csvBuilder = new StringBuilder();
            csvBuilder.AppendLine("AlbumID,AlbumCaption,BoardID");

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                foreach (var album in albums)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string boardId = await CreateBoardForAlbumAsync(
                        httpClient, album, progress, cancellationToken).ConfigureAwait(false);

                    string csvLine = BuildCsvLine(album.AlbumId, album.Caption, boardId);
                    csvBuilder.AppendLine(csvLine);
                }
            }

            File.WriteAllText(_csvPath, csvBuilder.ToString(), Encoding.UTF8);
            progress?.Report($"CSV file written to: {_csvPath}");
        }

        /// <summary>
        /// Loads all albums (EntityType = 'ALBUM') from DynamoDB via Scan.
        /// </summary>
        private async Task<List<AlbumInfo>> LoadAlbumsAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var result = new List<AlbumInfo>();

            var request = new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "EntityType = :albumType",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":albumType", new AttributeValue { S = "ALBUM" } }
                },
                ProjectionExpression = "ID, Caption, EntityType"
            };

            Dictionary<string, AttributeValue>? lastEvaluatedKey;

            do
            {
                request.ExclusiveStartKey = lastEvaluatedKey = request.ExclusiveStartKey;

                var response = await _dynamoDbClient.ScanAsync(request, cancellationToken).ConfigureAwait(false);
                lastEvaluatedKey = response.LastEvaluatedKey;

                foreach (var item in response.Items)
                {
                    if (!item.TryGetValue("ID", out var idAttr) || string.IsNullOrEmpty(idAttr.S))
                        continue;

                    var id = idAttr.S; // e.g. "ALB#0001"
                    if (!id.StartsWith("ALB#", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string albumNumber = id.Substring(4); // "0001"
                    string caption = item.ContainsKey("Caption") ? item["Caption"].S : string.Empty;

                    result.Add(new AlbumInfo
                    {
                        AlbumId = albumNumber,
                        Caption = caption
                    });
                }

                progress?.Report($"Scanned {result.Count} albums so far...");

            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            return result;
        }

        /// <summary>
        /// Creates a Pinterest board for the given album and returns the Board ID.
        /// </summary>
        private async Task<string> CreateBoardForAlbumAsync(
            HttpClient httpClient,
            AlbumInfo album,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            string boardName = string.IsNullOrWhiteSpace(album.Caption)
                ? $"Album {album.AlbumId}"
                : album.Caption;

            string boardDescription = $"Cross-stitch patterns from album {album.AlbumId}: {album.Caption}";

            var payload = new
            {
                name = boardName,
                description = boardDescription
            };

            string json = JsonConvert.SerializeObject(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("https://api.pinterest.com/v5/boards", content, cancellationToken)
                                           .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Failed to create board for album {album.AlbumId}: {response.StatusCode} - {body}");

            var boardResponse = JsonConvert.DeserializeObject<PinterestBoardResponse>(body);
            if (boardResponse == null || string.IsNullOrEmpty(boardResponse.Id))
                throw new Exception($"Pinterest board creation succeeded but ID is missing for album {album.AlbumId}. Response: {body}");

            progress?.Report($"Created board '{boardResponse.Name}' (ID={boardResponse.Id}) for album {album.AlbumId}.");
            return boardResponse.Id;
        }

        /// <summary>
        /// Builds a safe CSV line: AlbumID, "Caption", BoardID (Caption quoted, quotes doubled).
        /// </summary>
        private static string BuildCsvLine(string albumId, string caption, string boardId)
        {
            string safeCaption = (caption ?? string.Empty).Replace("\"", "\"\"");
            return $"{albumId},\"{safeCaption}\",{boardId}";
        }
    }
}

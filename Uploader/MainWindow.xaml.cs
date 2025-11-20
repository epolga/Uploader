using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using UploadPatterns;
using Uploader.Helpers;
using Message = Amazon.SimpleEmail.Model.Message;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;

namespace Uploader
{
    /// <summary>
    /// Main WPF window for uploading a cross-stitch design batch:
    /// - Reads PDF and extracts pattern info
    /// - Extracts and saves preview image
    /// - Uploads SCC, PDF and JPG to S3
    /// - Inserts item into DynamoDB
    /// - Creates a Pinterest pin
    /// - Sends notification email and reboots EC2 environment
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string _bucketName =
            ConfigurationManager.AppSettings["S3BucketName"] ?? "cross-stitch-designs";

        private readonly AmazonDynamoDBClient _dynamoDbClient = new AmazonDynamoDBClient();
        private readonly AmazonS3Client _s3Client = new AmazonS3Client();
        private readonly AmazonSimpleEmailServiceClient _sesClient = new AmazonSimpleEmailServiceClient();

        private readonly EC2Helper _ec2Helper =
            new EC2Helper(RegionEndpoint.USEast1, "cross-stitch-env");

        private readonly S3Helper _s3Helper =
            new S3Helper(RegionEndpoint.USEast1, "cross-stitch-designs");

        private readonly PinterestHelper _pinterestHelper = new PinterestHelper();
        private readonly TransferUtility _s3TransferUtility;

        private string _imageFilePath = string.Empty;
        private string _batchFolderPath = string.Empty;

        private const string PhotoPrefix = "photos";
        private int _albumId;

        public PatternInfo? PatternInfo { get; private set; }
        public string AlbumPartitionKey { get; private set; } = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            _s3TransferUtility = new TransferUtility(_s3Client);
        }

        /// <summary>
        /// Sets album internal fields based on album ID.
        /// </summary>
        private void SetAlbumInfo(int albumId)
        {
            _albumId = albumId;
            AlbumPartitionKey = $"ALB#{albumId:D4}";
        }

        #region Event handlers (UI thread)

        private async void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder",
                InitialDirectory = ConfigurationManager.AppSettings["InitialFolder"]
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            _batchFolderPath = dialog.SelectedPath;
            _imageFilePath = Path.Combine(_batchFolderPath, "4.jpg");

            // UI updates are safe here (we are on UI thread)
            txtFolderPath.Text = _batchFolderPath;

            try
            {
                PatternInfo = await CreatePatternInfoAsync();

                // Back on UI thread after await (no ConfigureAwait(false) here),
                // so we can safely update text boxes
                SetPatternInfoToUI(PatternInfo);
                string pdfPath = Path.Combine(_batchFolderPath, "1.pdf");
                GetAndShowImage(pdfPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while reading pattern info: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (PatternInfo == null)
            {
                txtStatus.Text = "Extract PDF info before upload.\r\n";
                return;
            }

            if (string.IsNullOrEmpty(_batchFolderPath) || string.IsNullOrEmpty(txtAlbumNumber.Text))
            {
                MessageBox.Show("Please select a folder and ensure AlbumID is loaded.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtStatus.Text = "Processing...\r\n";

            try
            {
                await RunFullUploadFlowAsync(PatternInfo);

                // Continuation is on UI thread (no ConfigureAwait(false) here)
                txtStatus.Text += "[Upload] Done.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Upload] Operation failed: {ex.Message}\r\n";
                txtStatus.Text += $"[Upload] Exception details: {ex}\r\n";

                MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnTestPinterest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Hard-coded test path – adjust if needed
                var info = new PatternInfo(@"D:\Stitch Craft\Charts\ReadyCharts\2025_11_02\1.pdf");
                string pinId = await _pinterestHelper.UploadPinExampleAsync(info);

                txtStatus.Text += $"[Test Pinterest] Pin created: {pinId}\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Test Pinterest] Failed: {ex.Message}\r\n";
            }
        }

        private async void BtnCreateBoards_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "Creating Pinterest boards from albums...\r\n";

            var creator = new PinterestBoardCreator();
            var progress = new Progress<string>(msg =>
            {
                // This callback may run on background threads, so we marshal to UI thread
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += msg + Environment.NewLine;
                }));
            });

            try
            {
                await creator.CreateBoardsAndCsvAsync(progress, CancellationToken.None);

                // Back on UI thread (no ConfigureAwait(false) at call site)
                txtStatus.Text += "Finished creating boards and CSV.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRenameBoards_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "Renaming Pinterest boards from CSV...\r\n";

            var renamer = new PinterestBoardRenamer();
            var progress = new Progress<string>(msg =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += msg + Environment.NewLine;
                }));
            });

            try
            {
                await renamer.RenameBoardsFromCsvAsync(progress, CancellationToken.None);
                txtStatus.Text += "Finished renaming boards.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error while renaming boards: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Pattern info and album helpers (no UI access inside)

        /// <summary>
        /// Creates PatternInfo from 1.pdf in the batch folder and enriches it
        /// with AlbumId, NPage and DesignID from DynamoDB.
        /// </summary>
        private async Task<PatternInfo> CreatePatternInfoAsync()
        {
            string pdfPath = Path.Combine(_batchFolderPath, "1.pdf");
            var patternInfo = new PatternInfo(pdfPath);

            patternInfo.AlbumId = LoadAlbumIdFromTxt();
            patternInfo.NPage = await GetNextNPageAsync();   
            patternInfo.DesignID = await GetNextDesignIdAsync();

            return patternInfo;
        }

        /// <summary>
        /// Copies pattern information into UI text boxes. Called only on UI thread.
        /// </summary>
        private void SetPatternInfoToUI(PatternInfo patternInfo)
        {
            txtTitle.Text = patternInfo.Title;
            txtNotes.Text = patternInfo.Notes;
            txtWidth.Text = patternInfo.Width.ToString();
            txtHeight.Text = patternInfo.Height.ToString();
            txtNColors.Text = patternInfo.NColors.ToString();
        }

        private int LoadAlbumIdFromTxt()
        {
            string? albumFile = Directory
                .GetFiles(_batchFolderPath, "*.txt")
                .FirstOrDefault();

            if (albumFile == null)
            {
                MessageBox.Show("Exactly one .txt file expected for AlbumID.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 0;
            }

            string albumIdStr = Path.GetFileNameWithoutExtension(albumFile);
            if (!int.TryParse(albumIdStr, out int albumId))
            {
                MessageBox.Show("Invalid AlbumID in .txt file.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 0;
            }

            // Update UI and internal fields on UI thread
            txtAlbumNumber.Text = albumId.ToString();
            SetAlbumInfo(albumId);

            return albumId;
        }

        #endregion

        #region Upload flow (no direct UI access)

        /// <summary>
        /// Full upload flow: S3, DynamoDB, Pinterest, SES, EC2 reboot.
        /// This method does not touch UI directly.
        /// </summary>
        private async Task RunFullUploadFlowAsync(PatternInfo patternInfo)
        {
            // 1. Calculate global page and next design ID
            int maxGlobalPage = await GetMaxGlobalPageAsync().ConfigureAwait(false);
            int nGlobalPage = maxGlobalPage + 1;

            string sccFile = GetSccFile();

            // Recalculate DesignID to avoid conflicts
            patternInfo.DesignID = await GetNextDesignIdAsync().ConfigureAwait(false);

            // 2. Upload files to S3
            await UploadChartToS3Async(patternInfo.DesignID, sccFile).ConfigureAwait(false);
            await UploadPdfToS3Async(patternInfo.DesignID).ConfigureAwait(false);
            await UploadImageToS3Async(patternInfo.DesignID).ConfigureAwait(false);

            // 3. Insert item into DynamoDB
            await InsertItemIntoDynamoDbAsync(
                patternInfo.NPage,
                patternInfo.DesignID,
                nGlobalPage).ConfigureAwait(false);

            // 4. Create Pinterest pin
            string pinId = await _pinterestHelper.UploadPinExampleAsync(patternInfo).ConfigureAwait(false);

            // 5. Notify admin via email
            await SendNotificationMailToAdminAsync(patternInfo.DesignID, pinId).ConfigureAwait(false);

            // 6. Reboot EC2 environment (status text is updated via callback which marshals to UI)
            await _ec2Helper.RebootInstancesRequest(msg =>
            {
                Dispatcher.BeginInvoke(new Action(() => { txtStatus.Text += msg; }));
            }).ConfigureAwait(false);
        }

        private string GetSccFile()
        {
            string? sccFile = Directory.GetFiles(_batchFolderPath, "*.scc").FirstOrDefault();
            if (sccFile == null)
            {
                throw new Exception(".scc file expected.");
            }

            return sccFile;
        }

        private async Task<int> GetMaxGlobalPageAsync()
        {
            var request = new QueryRequest
            {
                TableName = "CrossStitchItems",
                IndexName = "Designs-index",
                KeyConditionExpression = "EntityType = :et",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":et", new AttributeValue { S = "DESIGN" } }
                },
                ScanIndexForward = false,
                Limit = 1,
                ProjectionExpression = "NGlobalPage"
            };

            var response = await _dynamoDbClient.QueryAsync(request).ConfigureAwait(false);

            if (response.Items.Count > 0 && response.Items[0].ContainsKey("NGlobalPage"))
            {
                return int.Parse(response.Items[0]["NGlobalPage"].N);
            }

            return 0;
        }

        private async Task<string> GetNextNPageAsync()
        {
            var request = new QueryRequest
            {
                TableName = "CrossStitchItems",
                KeyConditionExpression = "ID = :id",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":id", new AttributeValue { S = AlbumPartitionKey } }
                },
                ScanIndexForward = false,
                Limit = 1,
                ProjectionExpression = "NPage"
            };

            var response = await _dynamoDbClient.QueryAsync(request).ConfigureAwait(false);

            int maxNPage = 0;
            if (response.Items.Count > 0 && response.Items[0].ContainsKey("NPage"))
            {
                string current = response.Items[0]["NPage"].S;
                string trimmed = current.TrimStart('0');
                maxNPage = string.IsNullOrEmpty(trimmed) ? 0 : int.Parse(trimmed);
            }

            return (maxNPage + 1).ToString("D5");
        }

        private async Task<int> GetNextDesignIdAsync()
        {
            var request = new QueryRequest
            {
                TableName = "CrossStitchItems",
                IndexName = "DesignsByID-index",
                KeyConditionExpression = "EntityType = :et",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":et", new AttributeValue { S = "DESIGN" } }
                },
                ScanIndexForward = false,
                Limit = 1,
                ProjectionExpression = "DesignID"
            };

            var response = await _dynamoDbClient.QueryAsync(request).ConfigureAwait(false);

            if (response.Items.Count > 0 && response.Items[0].ContainsKey("DesignID"))
            {
                return int.Parse(response.Items[0]["DesignID"].N) + 1;
            }

            return 1;
        }

        private async Task UploadChartToS3Async(int designId, string sccFilePath)
        {
            string paddedDesignId = designId.ToString("D5");
            string key = $"charts/{paddedDesignId}_{PatternInfo?.Title}.scc";

            var request = new TransferUtilityUploadRequest
            {
                FilePath = sccFilePath,
                BucketName = _bucketName,
                Key = key,
                ContentType = "text/scc"
            };

            await _s3TransferUtility.UploadAsync(request).ConfigureAwait(false);
        }

        private async Task UploadPdfToS3Async(int designId)
        {
            string pdfPath = Path.Combine(_batchFolderPath, "1.pdf");
            if (!File.Exists(pdfPath))
            {
                throw new Exception("1.pdf not found.");
            }

            string key = $"pdfs/{_albumId}/Stitch{designId}_Kit.pdf";

            var request = new TransferUtilityUploadRequest
            {
                FilePath = pdfPath,
                BucketName = _bucketName,
                Key = key,
                ContentType = "application/pdf"
            };

            await _s3TransferUtility.UploadAsync(request).ConfigureAwait(false);
        }

        private async Task UploadImageToS3Async(int designId)
        {
            string photoKey = GetPhotoKey(designId);

            var request = new TransferUtilityUploadRequest
            {
                FilePath = _imageFilePath,
                BucketName = _bucketName,
                Key = photoKey,
                ContentType = "image/jpeg"
            };

            await _s3TransferUtility.UploadAsync(request).ConfigureAwait(false);
        }

        private async Task InsertItemIntoDynamoDbAsync(string nPage, int designId, int nGlobalPage)
        {
            if (PatternInfo == null)
                throw new InvalidOperationException("PatternInfo is not initialized.");

            var item = new Dictionary<string, AttributeValue>
            {
                { "ID",          new AttributeValue { S = AlbumPartitionKey } },
                { "NPage",       new AttributeValue { S = nPage } },
                { "AlbumID",     new AttributeValue { N = _albumId.ToString() } },
                { "Caption",     new AttributeValue { S = PatternInfo.Title } },
                { "Description", new AttributeValue { S = PatternInfo.Description } },
                { "DesignID",    new AttributeValue { N = designId.ToString() } },
                { "EntityType",  new AttributeValue { S = "DESIGN" } },
                { "Height",      new AttributeValue { N = PatternInfo.Height.ToString() } },
                { "NColors",     new AttributeValue { N = PatternInfo.NColors.ToString() } },
                { "NDownloaded", new AttributeValue { N = "0" } },
                { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                { "Notes",       new AttributeValue { S = PatternInfo.Notes } },
                { "Width",       new AttributeValue { N = PatternInfo.Width.ToString() } }
            };

            var request = new PutItemRequest
            {
                TableName = "CrossStitchItems",
                Item = item
            };

            await _dynamoDbClient.PutItemAsync(request).ConfigureAwait(false);
        }

        private async Task SendNotificationMailToAdminAsync(int designId, string pinId)
        {
            string? sender = ConfigurationManager.AppSettings["SenderEmail"];
            string? admin = ConfigurationManager.AppSettings["AdminEmail"];

            if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(admin) || PatternInfo == null)
                return;

            var emailRequest = new SendEmailRequest
            {
                Source = sender,
                Destination = new Destination
                {
                    ToAddresses = new List<string> { admin }
                },
                Message = new Message
                {
                    Subject = new Content("Upload Successful"),
                    Body = new Body
                    {
                        Text = new Content(
                            $"The upload for album {_albumId} design {designId} ({PatternInfo.Title}) pinId {pinId} was successful.")
                    }
                }
            };

            await _sesClient.SendEmailAsync(emailRequest).ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += "Upload and insertion completed successfully. Starting reboot...\r\n";
            }));
        }

        private string GetPhotoKey(int designId)
        {
            string fileName = Path.GetFileName(_imageFilePath);
            return $"{PhotoPrefix}/{_albumId}/{designId}/{fileName}";
        }

        #endregion

        #region PDF image extraction (UI only at the end)

        /// <summary>
        /// Extracts images from the PDF and shows the first one in the UI.
        /// Also saves it to _imageFilePath as JPEG.
        /// </summary>
        private void GetAndShowImage(string pdfPath)
        {
            if (!File.Exists(pdfPath))
            {
                ShowError("No file " + pdfPath);
                return;
            }

            List<System.Drawing.Image> images;

            try
            {
                images = ExtractImages(pdfPath);
            }
            catch (Exception ex)
            {
                ShowError("Failed to extract images: " + ex.Message);
                return;
            }

            if (images.Count < 1)
            {
                ShowError("Failed to get Image");
                return;
            }

            using var bitmap = new System.Drawing.Bitmap(images[0]);
            bitmap.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);

            ImageSource imgSource = ToBitmapSource(bitmap);
            imgBatch.Source = imgSource;

            try
            {
                bitmap.Save(_imageFilePath, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            catch
            {
                ShowError("Could not save image file");
            }
        }

        private static List<System.Drawing.Image> ExtractImages(string pdfPath)
        {
            var images = new List<System.Drawing.Image>();

            using var reader = new PdfReader(pdfPath);
            for (int i = 0; i <= reader.XrefSize - 1; i++)
            {
                var obj = reader.GetPdfObject(i);
                if (obj == null || !obj.IsStream())
                    continue;

                var stream = (PRStream)obj;
                var subtype = stream.Get(PdfName.SUBTYPE);
                if (subtype == null || !PdfName.IMAGE.Equals(subtype))
                    continue;

                try
                {
                    var imgObj = new PdfImageObject(stream);
                    var img = imgObj.GetDrawingImage();
                    if (img != null)
                        images.Add(img);
                }
                catch
                {
                    // Ignore image that cannot be parsed
                }
            }

            return images;
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public static BitmapSource ToBitmapSource(System.Drawing.Bitmap source)
        {
            using var stream = new MemoryStream();
            source.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
            stream.Position = 0;

            var result = new BitmapImage();
            result.BeginInit();
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.StreamSource = stream;
            result.EndInit();
            result.Freeze();
            return result;
        }

        #endregion

        #region Optional: build DesignToAlbumMap CSV from S3

        private static async Task CreateDesignToAlbumMapAsync(
            AmazonS3Client s3Client,
            string bucketName,
            string s3Prefix)
        {
            var dynamoClient = new AmazonDynamoDBClient(RegionEndpoint.USEast1);
            _ = Table.LoadTable(dynamoClient, "CrossStitchItems"); // loaded but not used currently

            var listRequest = new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = $"{PhotoPrefix}/"
            };

            var paginator = s3Client.Paginators.ListObjectsV2(listRequest);
            File.AppendAllLines("DesignToAlbumMap.csv", new[] { "DesignID,AlbumID" });

            string prevDesignStr = string.Empty;
            string prevAlbum = string.Empty;

            var designToAlbumMap = new SortedDictionary<int, int>();

            await foreach (var obj in paginator.S3Objects.ConfigureAwait(false))
            {
                var key = obj.Key;
                if (key.Contains("by-page", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("private", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = key.Split('/');
                if (parts.Length < 4)
                    continue;

                var album = parts[1];
                var designStr = parts[2];

                if (designStr == prevDesignStr && album == prevAlbum)
                    continue;

                if (!int.TryParse(designStr, out int designId)) continue;
                if (!int.TryParse(album, out int albumId)) continue;

                designToAlbumMap[designId] = albumId;
                prevDesignStr = designStr;
                prevAlbum = album;
            }

            foreach (var kvp in designToAlbumMap)
            {
                File.AppendAllLines("DesignToAlbumMap.csv", new[] { $"{kvp.Key},{kvp.Value}" });
            }
        }

        #endregion
    }
}

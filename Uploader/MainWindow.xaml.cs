using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using iTextSharp.text.pdf.codec;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UploadPatterns;
using Uploader.Helpers;
using Message = Amazon.SimpleEmail.Model.Message;
using MessageBox = System.Windows.MessageBox;  // Alias for WPF MessageBox

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
        private readonly string _bucketName = ConfigurationManager.AppSettings["S3BucketName"] ?? "cross-stitch-designs";

        private readonly AmazonDynamoDBClient _dynamoDbClient = new AmazonDynamoDBClient();
        private readonly AmazonS3Client _s3Client = new AmazonS3Client();
        private readonly AmazonSimpleEmailServiceClient _sesClient = new AmazonSimpleEmailServiceClient();

        private string _imageFilePath = string.Empty;
        private string _batchFolderPath = string.Empty;
        private static readonly string PhotoPrefix = "photos";
        private int _albumId;

        public required PatternInfo m_patternInfo;
        public required string m_strAlbumPartitionKey;

        private readonly EC2Helper _ec2Helper = new EC2Helper(RegionEndpoint.USEast1, "cross-stitch-env");
        private readonly S3Helper _s3Helper = new S3Helper(RegionEndpoint.USEast1, "cross-stitch-designs");
        private readonly PinterestHelper _pinterestHelper = new PinterestHelper();
        private readonly TransferUtility _s3TransferUtility;

        public MainWindow()
        {
            InitializeComponent();
            _s3TransferUtility = new TransferUtility(_s3Client);
        }

        /// <summary>
        /// Sets the current album info: numeric ID and DynamoDB partition key.
        /// </summary>
        private void SetAlbumInfo(int albumId)
        {
            _albumId = albumId;
            m_strAlbumPartitionKey = $"ALB#{albumId.ToString("D4")}";
        }

        /// <summary>
        /// "Select folder" button click:
        /// - lets user choose batch folder,
        /// - sets batch and image paths,
        /// - creates PatternInfo,
        /// - populates UI,
        /// - extracts and shows preview image.
        /// </summary>
        private async void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
                dialog.InitialDirectory = ConfigurationManager.AppSettings["InitialFolder"];

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _batchFolderPath = dialog.SelectedPath;
                    txtFolderPath.Text = _batchFolderPath;
                    _imageFilePath = Path.Combine(_batchFolderPath, "4.jpg");

                    m_patternInfo = await CreatePatternInfo();
                    SetPatternInfoToUI(m_patternInfo);

                    string pdfPath = Path.Combine(_batchFolderPath, "1.pdf");
                    GetImage(pdfPath);
                }
            }
        }

        /// <summary>
        /// Copies pattern information into UI text boxes.
        /// </summary>
        private void SetPatternInfoToUI(PatternInfo patternInfo)
        {
            txtTitle.Text = patternInfo.Title;
            txtNotes.Text = patternInfo.Notes;
            txtWidth.Text = patternInfo.Width.ToString();
            txtHeight.Text = patternInfo.Height.ToString();
            txtNColors.Text = patternInfo.NColors.ToString();
        }

        /// <summary>
        /// Creates PatternInfo from 1.pdf in the batch folder and enriches it
        /// with AlbumId, NPage and DesignID from DynamoDB.
        /// </summary>
        private async Task<PatternInfo> CreatePatternInfo()
        {
            var pdfPath = Path.Combine(_batchFolderPath, "1.pdf");
            var patternInfo = new PatternInfo(pdfPath);

            patternInfo.AlbumId = LoadAlbumId();
            patternInfo.NPage = await GetNPage();
            patternInfo.DesignID = await GetDesignId();

            return patternInfo;
        }

        /// <summary>
        /// Returns path to the only .scc file in the batch folder.
        /// </summary>
        private string GetSccFile()
        {
            string? sccFile = Directory.GetFiles(_batchFolderPath, "*.scc").FirstOrDefault();
            if (sccFile == null)
            {
                throw new Exception(".scc file expected.");
            }

            return sccFile;
        }

        /// <summary>
        /// Gets the current maximum NGlobalPage among designs, to append new global page number.
        /// </summary>
        private async Task<int> GetMaxGlobalPage()
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

            var response = await _dynamoDbClient.QueryAsync(request);

            if (response.Items.Count > 0 && response.Items[0].ContainsKey("NGlobalPage"))
            {
                return int.Parse(response.Items[0]["NGlobalPage"].N);
            }

            return 0;
        }

        /// <summary>
        /// Gets the next NPage within the current album partition.
        /// </summary>
        private async Task<string> GetNPage()
        {
            var request = new QueryRequest
            {
                TableName = "CrossStitchItems",
                KeyConditionExpression = "ID = :id",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":id", new AttributeValue { S = m_strAlbumPartitionKey } }
                },
                ScanIndexForward = false,
                Limit = 1,
                ProjectionExpression = "NPage"
            };

            var response = await _dynamoDbClient.QueryAsync(request);
            int maxNPage = 0;

            if (response.Items.Count > 0 && response.Items[0].ContainsKey("NPage"))
            {
                string current = response.Items[0]["NPage"].S;
                string trimmed = current.TrimStart('0');
                maxNPage = string.IsNullOrEmpty(trimmed) ? 0 : int.Parse(trimmed);
            }

            return (maxNPage + 1).ToString("D5");
        }

        /// <summary>
        /// Gets the next available DesignID.
        /// </summary>
        private async Task<int> GetDesignId()
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

            var response = await _dynamoDbClient.QueryAsync(request);

            if (response.Items.Count > 0 && response.Items[0].ContainsKey("DesignID"))
            {
                return int.Parse(response.Items[0]["DesignID"].N) + 1;
            }

            return 1;
        }

        /// <summary>
        /// Uploads SCC chart to S3.
        /// </summary>
        private async Task UploadChartToS3(int designId, string sccFilePath)
        {
            string paddedDesignId = designId.ToString("D5");
            string key = $"charts/{paddedDesignId}_{m_patternInfo.Title}.scc";

            var request = new TransferUtilityUploadRequest
            {
                FilePath = sccFilePath,
                BucketName = _bucketName,
                Key = key,
                ContentType = "text/scc"
            };

            await _s3TransferUtility.UploadAsync(request);
        }

        /// <summary>
        /// Uploads 1.pdf to S3.
        /// </summary>
        private async Task UploadPdfToS3(int designId)
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

            await _s3TransferUtility.UploadAsync(request);
        }

        /// <summary>
        /// Uploads JPG preview to S3.
        /// </summary>
        private async Task UploadImageToS3(int designId)
        {
            string photoKey = GetPhotoKey(designId);

            var request = new TransferUtilityUploadRequest
            {
                FilePath = _imageFilePath,
                BucketName = _bucketName,
                Key = photoKey,
                ContentType = "image/jpeg"
            };

            await _s3TransferUtility.UploadAsync(request);
        }

        /// <summary>
        /// Inserts a design item into DynamoDB CrossStitchItems table.
        /// </summary>
        private async Task InsertItemIntoDynamoDbAsync(string nPage, int designId, int nGlobalPage)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                { "ID",          new AttributeValue { S = m_strAlbumPartitionKey } },
                { "NPage",       new AttributeValue { S = nPage } },
                { "AlbumID",     new AttributeValue { N = _albumId.ToString() } },
                { "Caption",     new AttributeValue { S = m_patternInfo.Title } },
                { "Description", new AttributeValue { S = m_patternInfo.Description } },
                { "DesignID",    new AttributeValue { N = designId.ToString() } },
                { "EntityType",  new AttributeValue { S = "DESIGN" } },
                { "Height",      new AttributeValue { N = m_patternInfo.Height.ToString() } },
                { "NColors",     new AttributeValue { N = m_patternInfo.NColors.ToString() } },
                { "NDownloaded", new AttributeValue { N = "0" } },
                { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                { "Notes",       new AttributeValue { S = m_patternInfo.Notes } },
                { "Width",       new AttributeValue { N = m_patternInfo.Width.ToString() } }
            };

            var request = new PutItemRequest
            {
                TableName = "CrossStitchItems",
                Item = item
            };

            await _dynamoDbClient.PutItemAsync(request);
        }

        /// <summary>
        /// Sends SES notification email to admin about successful upload.
        /// </summary>
        private void SendNotificationMailToAdmin(int designId, string pinId)
        {
            var emailRequest = new SendEmailRequest
            {
                Source = ConfigurationManager.AppSettings["SenderEmail"],
                Destination = new Destination
                {
                    ToAddresses = new List<string> { ConfigurationManager.AppSettings["AdminEmail"] }
                },
                Message = new Message
                {
                    Subject = new Content("Upload Successful"),
                    Body = new Body
                    {
                        Text = new Content(
                            $"The upload for album {_albumId} design {designId} ({m_patternInfo.Title}) pinId {pinId} was successful.")
                    }
                }
            };

            _sesClient.SendEmailAsync(emailRequest);
            txtStatus.Text = "Upload and insertion completed successfully. Starting reboot...\r\n";
        }

        /// <summary>
        /// Returns S3 key for preview photo.
        /// </summary>
        private string GetPhotoKey(int designId)
        {
            string fileName = Path.GetFileName(_imageFilePath);
            return $"{PhotoPrefix}/{_albumId}/{designId}/{fileName}";
        }

        /// <summary>
        /// Loads album ID from single .txt file in batch folder, validates and sets album context.
        /// </summary>
        private int LoadAlbumId()
        {
            string? albumFile = Directory.GetFiles(_batchFolderPath, "*.txt").FirstOrDefault();
            int albumId = 0;

            if (albumFile != null)
            {
                string albumIdStr = Path.GetFileNameWithoutExtension(albumFile);
                if (int.TryParse(albumIdStr, out albumId))
                {
                    txtAlbumNumber.Text = albumId.ToString();
                    SetAlbumInfo(albumId);
                }
                else
                {
                    MessageBox.Show("Invalid AlbumID in .txt file.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Exactly one .txt file expected for AlbumID.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return albumId;
        }

        /// <summary>
        /// Upload button click: full upload flow to S3, DynamoDB, Pinterest, SES, EC2 reboot.
        /// </summary>
        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (m_patternInfo == null)
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
                int nGlobalPage = await GetMaxGlobalPage() + 1;
                string sccFile = GetSccFile();

                // Recalculate DesignID again (kept same logic as original code).
                m_patternInfo.DesignID = await GetDesignId();

                if (!int.TryParse(m_patternInfo.NPage, out var nPage))
                {
                    nPage = 0;
                }

                txtStatus.Text += $"[Upload] DesignID: {m_patternInfo.DesignID}, NPage: {m_patternInfo.NPage}, NGlobalPage: {nGlobalPage}\r\n";

                await UploadChartToS3(m_patternInfo.DesignID, sccFile);
                await UploadPdfToS3(m_patternInfo.DesignID);
                await UploadImageToS3(m_patternInfo.DesignID);
                await InsertItemIntoDynamoDbAsync(m_patternInfo.NPage, m_patternInfo.DesignID, nGlobalPage);

                txtStatus.Text += "[Upload] Files uploaded and DynamoDB item inserted.\r\n";

                string pinId = await _pinterestHelper.UploadPinExampleAsync(m_patternInfo);

                SendNotificationMailToAdmin(m_patternInfo.DesignID, pinId);

                await _ec2Helper.RebootInstancesRequest(
                    msg => Dispatcher.Invoke(() => txtStatus.Text += msg));
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Upload] Operation failed: {ex.Message}\r\n";
                txtStatus.Text += $"[Upload] Exception details: {ex}\r\n";

                MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Simple test handler that calls PinterestHelper for a hard-coded PDF.
        /// </summary>
        private async void BtnTestPinterest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var patternInfo = new PatternInfo(@"D:\Stitch Craft\Charts\ReadyCharts\2025_11_02\1.pdf");
                patternInfo.PinId = await _pinterestHelper.UploadPinExampleAsync(patternInfo);
                txtStatus.Text += $"[Test Pinterest] Pin created: {patternInfo.PinId}\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Test Pinterest] Failed: {ex.Message}\r\n";
            }
        }

        /// <summary>
        /// Creates Pinterest boards based on albums from DynamoDB and writes CSV mapping.
        /// </summary>
        private async void BtnCreateBoards_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "Creating Pinterest boards from albums...\r\n";

            var creator = new PinterestBoardCreator();
            var progress = new Progress<string>(msg => txtStatus.Text += msg + Environment.NewLine);

            try
            {
                await creator.CreateBoardsAndCsvAsync(progress, CancellationToken.None);
                txtStatus.Text += "Finished creating boards and CSV.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Renames Pinterest boards using AlbumBoards.csv.
        /// </summary>
        private async void BtnRenameBoards_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "Renaming Pinterest boards from CSV...\r\n";

            var renamer = new PinterestBoardRenamer();
            var progress = new Progress<string>(msg =>
            {
                txtStatus.Text += msg + Environment.NewLine;
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

        /// <summary>
        /// Extracts all images from a PDF file into a list of System.Drawing.Image.
        /// </summary>
        private List<System.Drawing.Image> ExtractImages(string pdfPath)
        {
            var images = new List<System.Drawing.Image>();
            iTextSharp.text.pdf.RandomAccessFileOrArray? raf = null;
            iTextSharp.text.pdf.PdfReader? reader = null;

            try
            {
                raf = new iTextSharp.text.pdf.RandomAccessFileOrArray(pdfPath);
                reader = new iTextSharp.text.pdf.PdfReader(raf, null);

                for (int i = 0; i <= reader.XrefSize - 1; i++)
                {
                    var pdfObj = reader.GetPdfObject(i);
                    if (pdfObj == null || !pdfObj.IsStream())
                        continue;

                    var pdfStream = (iTextSharp.text.pdf.PdfStream)pdfObj;
                    var subtype = pdfStream.Get(iTextSharp.text.pdf.PdfName.SUBTYPE);

                    if (subtype == null ||
                        subtype.ToString() != iTextSharp.text.pdf.PdfName.IMAGE.ToString())
                    {
                        continue;
                    }

                    try
                    {
                        var imgObj = new iTextSharp.text.pdf.parser.PdfImageObject(
                            (iTextSharp.text.pdf.PRStream)pdfStream);

                        images.Add(imgObj.GetDrawingImage());
                    }
                    catch
                    {
                        // Ignore images that cannot be parsed.
                    }
                }

                reader.Close();
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

            return images;
        }

        /// <summary>
        /// Extracts first image from PDF, shows it in the UI and saves JPG to _imageFilePath.
        /// </summary>
        private void GetImage(string pdfPath)
        {
            if (!File.Exists(pdfPath))
            {
                ShowMessage($"No file {pdfPath}");
                return;
            }

            List<System.Drawing.Image> images = ExtractImages(pdfPath);
            if (images.Count < 1)
            {
                ShowMessage("Failed to get Image");
                return;
            }

            var bitmap = new System.Drawing.Bitmap(images[0]);
            bitmap.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
            ImageSource imageSource = ToBitmapSource(bitmap);
            imgBatch.Source = imageSource;

            try
            {
                bitmap.Save(_imageFilePath, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            catch
            {
                ShowMessage("Could not save image file");
            }
        }

        /// <summary>
        /// Shows a WPF error message box.
        /// </summary>
        private static void ShowMessage(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Converts System.Drawing.Bitmap to WPF BitmapSource.
        /// </summary>
        public static BitmapSource ToBitmapSource(System.Drawing.Bitmap source)
        {
            using (var stream = new MemoryStream())
            {
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
        }

        /// <summary>
        /// Optional helper to create a Design → Album mapping CSV from S3 keys.
        /// </summary>
        private static async Task CreateDesignToAlbumMap(AmazonS3Client s3Client, string bucketName, string s3Prefix)
        {
            var dynamoClient = new AmazonDynamoDBClient(RegionEndpoint.USEast1);
            var table = Table.LoadTable(dynamoClient, "CrossStitchItems");

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

            await foreach (var obj in paginator.S3Objects)
            {
                var key = obj.Key;
                if (key.Contains("by-page") || key.Contains("private"))
                    continue;

                var parts = key.Split('/');
                if (parts.Length < 4)
                    continue;

                var album = parts[1];
                var designStr = parts[2];

                if (designStr == prevDesignStr && album == prevAlbum)
                    continue;

                if (!int.TryParse(designStr, out var designId)) continue;
                if (!int.TryParse(album, out var albumId)) continue;

                designToAlbumMap[designId] = albumId;
                prevDesignStr = designStr;
                prevAlbum = album;
            }

            foreach (var kvp in designToAlbumMap)
            {
                File.AppendAllLines("DesignToAlbumMap.csv", new[] { $"{kvp.Key},{kvp.Value}" });
            }
        }
    }
}

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
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UploadPatterns;
using Uploader.Helpers;
using Message = Amazon.SimpleEmail.Model.Message; // Explicit alias for SES Message

namespace Uploader
{
    public partial class MainWindow : Window
    {
        private string m_strBucketName = ConfigurationManager.AppSettings["S3Bucket"] ?? "cross-stitch-designs";
        private readonly AmazonDynamoDBClient dynamoDbClient = new AmazonDynamoDBClient();
        private readonly AmazonS3Client s3Client = new AmazonS3Client();
        private readonly AmazonSimpleEmailServiceClient sesClient = new AmazonSimpleEmailServiceClient();

        string m_strImageFileName = string.Empty;
        string m_strBatchFolder = string.Empty;
        static string m_photoPrefix = "photos";
        int m_iAlbumId = 0;

        required public PatternInfo m_patternInfo;
        required public string m_strAlbumPartitionKey;

        EC2Helper m_ec2Helper = new EC2Helper(RegionEndpoint.USEast1, "cross-stitch-env");
        S3Helper m_s3Helper = new S3Helper(RegionEndpoint.USEast1, "cross-stitch-designs");

        private readonly PinterestHelper pinterestHelper = new PinterestHelper();
        TransferUtility m_s3TransferUtility;

        public MainWindow()
        {
            InitializeComponent();
            m_s3TransferUtility = new TransferUtility(s3Client);
        }

        void SetAlbumInfo(int albumId)
        {
            m_iAlbumId = albumId;
            m_strAlbumPartitionKey = $"ALB#{albumId.ToString("D4")}";
        }

        void GetPDF(string strPDFFile)
        {
            m_patternInfo = new PatternInfo(strPDFFile);
            txtTitle.Text = m_patternInfo.Title;
            txtNotes.Text = m_patternInfo.Notes;
            txtWidth.Text = m_patternInfo.Width.ToString();
            txtHeight.Text = m_patternInfo.Height.ToString();
            txtNColors.Text = m_patternInfo.NColors.ToString();
            GetImage(strPDFFile);
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
                dialog.InitialDirectory = ConfigurationManager.AppSettings["InitialFolder"];
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    m_strBatchFolder = dialog.SelectedPath;
                    txtFolderPath.Text = m_strBatchFolder;
                    m_strImageFileName = Path.Combine(m_strBatchFolder, "4.jpg");

                    LoadAlbumId();
                    GetPDF(Path.Combine(m_strBatchFolder, "1.pdf"));
                }
            }
        }

        string GetSccFile()
        {
            string? sccFile = Directory.GetFiles(m_strBatchFolder, "*.scc").FirstOrDefault();
            if (sccFile == null)
                throw new Exception(".scc file expected.");
            return sccFile;
        }

        async Task<int> GetMaxGlobalPage()
        {
            var maxGlobalPageRequest = new QueryRequest
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

            var maxGlobalPageResponse = await dynamoDbClient.QueryAsync(maxGlobalPageRequest);
            return maxGlobalPageResponse.Items.Count > 0
                ? int.Parse(maxGlobalPageResponse.Items[0]["NGlobalPage"].N)
                : 0;
        }

        async Task<string> GetNPage()
        {
            var maxNPageRequest = new QueryRequest
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

            var maxNPageResponse = await dynamoDbClient.QueryAsync(maxNPageRequest);
            int maxNPage = 0;

            if (maxNPageResponse.Items.Count > 0 && maxNPageResponse.Items[0].ContainsKey("NPage"))
            {
                string maxNPageStr = maxNPageResponse.Items[0]["NPage"].S;
                string trimmed = maxNPageStr.TrimStart('0');
                maxNPage = string.IsNullOrEmpty(trimmed) ? 0 : int.Parse(trimmed);
            }

            return (maxNPage + 1).ToString("D5");
        }

        async Task<int> GetDesignId()
        {
            var maxDesignIdRequest = new QueryRequest
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

            var maxDesignIdResponse = await dynamoDbClient.QueryAsync(maxDesignIdRequest);
            return maxDesignIdResponse.Items.Count > 0
                ? int.Parse(maxDesignIdResponse.Items[0]["DesignID"].N) + 1
                : 1;
        }

        async void UploadChart(int designId, string sccFilePath)
        {
            string paddedDesignId = designId.ToString("D5");
            string key = $"charts/{paddedDesignId}_{m_patternInfo.Title}.scc";

            await m_s3TransferUtility.UploadAsync(new TransferUtilityUploadRequest
            {
                FilePath = sccFilePath,
                BucketName = m_strBucketName,
                Key = key,
                ContentType = "text/scc"
            });
        }

        async void UploadPDF(int designId)
        {
            string pdfFile = Path.Combine(m_strBatchFolder, "1.pdf");
            if (!File.Exists(pdfFile))
                throw new Exception("1.pdf not found.");

            string pdfKey = $"pdfs/{m_iAlbumId}/Stitch{designId}_Kit.pdf";

            await m_s3TransferUtility.UploadAsync(new TransferUtilityUploadRequest
            {
                FilePath = pdfFile,
                BucketName = m_strBucketName,
                Key = pdfKey,
                ContentType = "application/pdf"
            });
        }

        async void UploadImage(int iDesignId)
        {
            string photoKey = GetPhotoKey(iDesignId);

            await m_s3TransferUtility.UploadAsync(new TransferUtilityUploadRequest
            {
                FilePath = m_strImageFileName,
                BucketName = m_strBucketName,
                Key = photoKey,
                ContentType = "image/jpeg"
            });
        }

        void InsertItemIntoDynamoDB(string nPage, int designId, int nGlobalPage)
        {
            var request = new PutItemRequest
            {
                TableName = "CrossStitchItems",
                Item = new Dictionary<string, AttributeValue>
                {
                    { "ID", new AttributeValue { S = m_strAlbumPartitionKey } },
                    { "NPage", new AttributeValue { S = nPage } },
                    { "AlbumID", new AttributeValue { N = m_iAlbumId.ToString() } },
                    { "Caption", new AttributeValue { S = m_patternInfo.Title } },
                    { "Description", new AttributeValue { S = m_patternInfo.Description } },
                    { "DesignID", new AttributeValue { N = designId.ToString() } },
                    { "EntityType", new AttributeValue { S = "DESIGN" } },
                    { "Height", new AttributeValue { N = m_patternInfo.Height.ToString() } },
                    { "NColors", new AttributeValue { N = m_patternInfo.NColors.ToString() } },
                    { "NDownloaded", new AttributeValue { N = "0" } },
                    { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                    { "Notes", new AttributeValue { S = m_patternInfo.Notes } },
                    { "Width", new AttributeValue { N = m_patternInfo.Width.ToString() } },
                }
            };

            dynamoDbClient.PutItemAsync(request);
        }

        async Task<string> CreatePinterestPin(int iDesignId)
        {
            string pinId = string.Empty;
            try
            {
                string strPhotoKey = GetPhotoKey(iDesignId);
                string imageUrl = $"https://{m_strBucketName}.s3.amazonaws.com/{strPhotoKey}";

                txtStatus.Text += $"[Pinterest] Creating pin for design {iDesignId}...\r\n";
                txtStatus.Text += $"[Pinterest] Image URL: {imageUrl}\r\n";

                pinId = await pinterestHelper.CreatePinAsync(imageUrl, m_patternInfo);

                txtStatus.Text += $"[Pinterest] Pin created successfully. ID: {pinId}\r\n";
            }
            catch (Exception pinEx)
            {
                txtStatus.Text += $"[Pinterest] Pin creation failed: {pinEx.Message}\r\n";
                txtStatus.Text += $"[Pinterest] Exception details: {pinEx}\r\n";
            }
            return pinId;
        }

        void SendNotificationMailToAdmin(int iDesignId, string pinId)
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
                            $"The upload for album {m_iAlbumId} design {iDesignId} ({m_patternInfo.Title}) pinId {pinId} was successful.")
                    }
                }
            };

            sesClient.SendEmailAsync(emailRequest);
            txtStatus.Text = "Upload and insertion completed successfully. Starting reboot...\r\n";
        }

        private string GetPhotoKey(int iDesignId)
        {
            string photoFileName = Path.GetFileName(m_strImageFileName);
            return $"{m_photoPrefix}/{m_iAlbumId}/{iDesignId}/{photoFileName}";
        }

        private void LoadAlbumId()
        {
            string? albumFile = Directory.GetFiles(m_strBatchFolder, "*.txt").FirstOrDefault();

            if (albumFile != null)
            {
                string albumIdStr = Path.GetFileNameWithoutExtension(albumFile);
                if (int.TryParse(albumIdStr, out int albumId))
                {
                    txtAlbumNumber.Text = albumId.ToString();
                    SetAlbumInfo(albumId);
                }
                else
                {
                    System.Windows.MessageBox.Show("Invalid AlbumID in .txt file.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Exactly one .txt file expected for AlbumID.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (m_patternInfo == null)
            {
                txtStatus.Text = "Extract PDF info before upload.\r\n";
                return;
            }

            if (string.IsNullOrEmpty(m_strBatchFolder) || string.IsNullOrEmpty(txtAlbumNumber.Text))
            {
                System.Windows.MessageBox.Show("Please select a folder and ensure AlbumID is loaded.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtStatus.Text = "Processing...\r\n";

            try
            {
                int nGlobalPage = await GetMaxGlobalPage() + 1;
                string strNPage = await GetNPage();
                string sccFile = GetSccFile();
                int iDesignId = await GetDesignId();

                txtStatus.Text += $"[Upload] DesignID: {iDesignId}, NPage: {strNPage}, NGlobalPage: {nGlobalPage}\r\n";

                UploadChart(iDesignId, sccFile);
                UploadPDF(iDesignId);
                UploadImage(iDesignId);
                InsertItemIntoDynamoDB(strNPage, iDesignId, nGlobalPage);

                txtStatus.Text += "[Upload] Files uploaded and DynamoDB item inserted.\r\n";

                string pinId = await CreatePinterestPin(iDesignId);
                SendNotificationMailToAdmin(iDesignId, pinId);

                await m_ec2Helper.RebootInstancesRequest(
                    msg => Dispatcher.Invoke(() => txtStatus.Text += msg));
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Upload] Operation failed: {ex.Message}\r\n";
                txtStatus.Text += $"[Upload] Exception details: {ex}\r\n";

                System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnTestPinterest_Click(object sender, RoutedEventArgs e)
        {
            pinterestHelper.UploadPin();
            return;

            txtStatus.Text += "[Test] Starting Pinterest integration test...\r\n";

            try
            {
                if (m_patternInfo == null)
                {
                    txtStatus.Text += "[Test] PatternInfo is null. Load a PDF first.\r\n";
                    return;
                }

                if (string.IsNullOrEmpty(m_strImageFileName) || !File.Exists(m_strImageFileName))
                {
                    txtStatus.Text += "[Test] Image file not found. Select folder and load PDF first.\r\n";
                    return;
                }

                string testKey = $"test/pinterest/{DateTime.UtcNow:yyyyMMddHHmmssfff}.jpg";
                txtStatus.Text += $"[Test] Uploading test image to S3 key: {testKey}\r\n";

                await m_s3TransferUtility.UploadAsync(new TransferUtilityUploadRequest
                {
                    FilePath = m_strImageFileName,
                    BucketName = m_strBucketName,
                    Key = testKey,
                    ContentType = "image/jpeg"
                });

                string imageUrl = $"https://{m_strBucketName}.s3.amazonaws.com/{testKey}";
                txtStatus.Text += $"[Test] Test image URL: {imageUrl}\r\n";
                txtStatus.Text += "[Test] Calling Pinterest API...\r\n";

                string pinId = await pinterestHelper.CreatePinAsync(imageUrl, m_patternInfo);

                txtStatus.Text += $"[Test] Pinterest Pin created successfully. ID: {pinId}\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Test] Pinterest test failed: {ex.Message}\r\n";
                txtStatus.Text += $"[Test] Exception details: {ex}\r\n";
            }
        }

        private List<System.Drawing.Image> ExtractImages(string PDFSourcePath)
        {
            List<System.Drawing.Image> ImgList = new List<System.Drawing.Image>();

            iTextSharp.text.pdf.RandomAccessFileOrArray? RAFObj = null;
            iTextSharp.text.pdf.PdfReader? PDFReaderObj = null;

            try
            {
                RAFObj = new iTextSharp.text.pdf.RandomAccessFileOrArray(PDFSourcePath);
                PDFReaderObj = new iTextSharp.text.pdf.PdfReader(RAFObj, null);

                for (int i = 0; i <= PDFReaderObj.XrefSize - 1; i++)
                {
                    var PDFObj = PDFReaderObj.GetPdfObject(i);

                    if ((PDFObj != null) && PDFObj.IsStream())
                    {
                        var PDFStreamObj = (iTextSharp.text.pdf.PdfStream)PDFObj;
                        var subtype = PDFStreamObj.Get(iTextSharp.text.pdf.PdfName.SUBTYPE);

                        if ((subtype != null) &&
                            subtype.ToString() == iTextSharp.text.pdf.PdfName.IMAGE.ToString())
                        {
                            try
                            {
                                var PdfImageObj =
                                    new iTextSharp.text.pdf.parser.PdfImageObject(
                                        (iTextSharp.text.pdf.PRStream)PDFStreamObj);

                                ImgList.Add(PdfImageObj.GetDrawingImage());
                            }
                            catch { }
                        }
                    }
                }

                PDFReaderObj.Close();
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

            return ImgList;
        }

        void GetImage(string strPDFFileName)
        {
            if (!File.Exists(strPDFFileName))
            {
                ShowMessage($"No file {strPDFFileName}");
                return;
            }

            List<System.Drawing.Image> lstImages = ExtractImages(strPDFFileName);
            if (lstImages.Count < 1)
            {
                ShowMessage("Failed to get Image");
                return;
            }

            System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(lstImages[0]);
            bitmap.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
            ImageSource imageSource = ToBitmapSource(bitmap);
            imgBatch.Source = imageSource;

            try
            {
                bitmap.Save(m_strImageFileName, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            catch
            {
                ShowMessage("Could not save image file");
            }
        }

        void ShowMessage(string message)
        {
            System.Windows.MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public static BitmapSource ToBitmapSource(System.Drawing.Bitmap source)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                source.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
                stream.Position = 0;

                BitmapImage result = new BitmapImage();
                result.BeginInit();
                result.CacheOption = BitmapCacheOption.OnLoad;
                result.StreamSource = stream;
                result.EndInit();
                result.Freeze();
                return result;
            }
        }

        static async Task CreateDesignToAlbumMap(AmazonS3Client s3Client, string bucketName, string s3Prefix)
        {
            var dynamoClient = new AmazonDynamoDBClient(RegionEndpoint.USEast1);
            var table = Table.LoadTable(dynamoClient, "CrossStitchItems");

            var listRequest = new ListObjectsV2Request
            {
                BucketName = "cross-stitch-designs",
                Prefix = $"{m_photoPrefix}/"
            };

            var paginator = s3Client.Paginators.ListObjectsV2(listRequest);
            File.AppendAllLines("DesignToAlbumMap.csv", new[] { "DesignID,AlbumID" });

            string prevDesignStr = "";
            string prevAlbum = "";

            SortedDictionary<int, int> designToAlbumMap = new();

            await foreach (var obj in paginator.S3Objects)
            {
                var key = obj.Key;
                if (key.Contains("by-page") || key.Contains("private"))
                    continue;

                var parts = key.Split('/');
                if (parts.Length < 4) continue;

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

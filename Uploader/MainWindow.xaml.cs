// MainWindow.xaml.cs
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Transfer;
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
using Amazon.SimpleEmail.Model;
using Amazon.SimpleEmail;
using Message = Amazon.SimpleEmail.Model.Message;
using Amazon.S3.Model;
using Amazon;
using Amazon.DynamoDBv2.DocumentModel;
using Uploader.Helpers;
using System.Net.Http; // If not already present
using Newtonsoft.Json; // If not already present

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
        private readonly PinterestHelper pinterestHelper = new PinterestHelper(); // Add this
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
            // Find .scc file for DesignName
            string? sccFile = Directory.GetFiles(m_strBatchFolder, "*.scc").FirstOrDefault();
            if (sccFile == null)
            {
                throw new Exception(".scc file expected.");
            }
            return sccFile;
        }

        async Task<int> GetMaxGlobalPage()
        {
            // Get max NGlobalPage
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
            int nGlobalPage = maxGlobalPageResponse.Items.Count > 0
                ? int.Parse(maxGlobalPageResponse.Items[0]["NGlobalPage"].N)
                : 0;
            return nGlobalPage;
        }

        async Task<string> GetNPage()
        {
            // Get max NPage for the album
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
            int newNPageNum = maxNPage + 1;
            string nPage = newNPageNum.ToString("D5"); // Pad with leading zeros to 5 characters
            return nPage;
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
            int designId = maxDesignIdResponse.Items.Count > 0
                ? int.Parse(maxDesignIdResponse.Items[0]["DesignID"].N) + 1
                : 1;
            return designId;
        }

        async void UploadChart(int designId, string sccFilePath)
        {
            string paddedDesignId = designId.ToString("D5");
            string sccKey = $"charts/{paddedDesignId}_{m_patternInfo.Title}.scc";
            var sccUploadRequest = new TransferUtilityUploadRequest
            {
                FilePath = sccFilePath,
                BucketName = m_strBucketName,
                Key = sccKey,
                ContentType = "text/scc"
            };
            
            await m_s3TransferUtility.UploadAsync(sccUploadRequest);
        }

        async void UploadPDF(int designId)
        {
            string pdfFile = Path.Combine(m_strBatchFolder, "1.pdf");
            if (!File.Exists(pdfFile))
            {
                throw new Exception("1.pdf not found.");
            }

            string pdfKey = $"pdfs/{m_iAlbumId}/Stitch{designId}_Kit.pdf";
            var pdfUploadRequest = new TransferUtilityUploadRequest
            {
                FilePath = pdfFile,
                BucketName = m_strBucketName,
                Key = pdfKey,
                ContentType = "application/pdf"
            };
            await m_s3TransferUtility.UploadAsync(pdfUploadRequest);
        }

        async void UploadImage(int iDesignId)
        {
            string photoKey = GetPhotoKey(iDesignId);

            var photoUploadRequest = new TransferUtilityUploadRequest
            {
                FilePath = m_strImageFileName,
                BucketName = m_strBucketName,
                Key = photoKey,
                ContentType = "image/jpeg",
            };
            await m_s3TransferUtility.UploadAsync(photoUploadRequest);
        }

        void InsertItemIntoDynamoDB(string nPage, int designId, int nGlobalPage)
        {
            var putItemRequest = new PutItemRequest
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
                        { "Height", new AttributeValue { N = m_patternInfo.Height.ToString()} },
                        { "NColors", new AttributeValue { N = m_patternInfo.NColors.ToString()} },
                        { "NDownloaded", new AttributeValue { N = "0"} },
                        { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                        { "Notes", new AttributeValue { S = m_patternInfo.Notes } },
                        { "Width", new AttributeValue { N = m_patternInfo.Width.ToString()} },
                    }
            };
            dynamoDbClient.PutItemAsync(putItemRequest);
        }
        
        async Task<string> CreatePinterestPin(int iDesignId)
        {
            string pinId = String.Empty;
            try
            {
             /*   string strPhotoKey = GetPhotoKey(iDesignId);
                string imageUrl = $"https://{m_strBucketName}.s3.amazonaws.com/{strPhotoKey}";

                pinId = await pinterestHelper.CreatePinAsync(imageUrl, m_patternInfo);
                txtStatus.Text += $"Pinterest Pin created successfully (ID: {pinId}).\r\n";*/
            }
            catch (Exception pinEx)
            {
                txtStatus.Text += $"Pinterest Pin creation failed: {pinEx.Message}\r\n";
            }
            return pinId;
        }

        void SendNotificationMailToAdmin(int iDesignId, string pinId)
        {   
            var emailRequest = new SendEmailRequest
            {
                Source = ConfigurationManager.AppSettings["SenderEmail"],
                Destination = new Destination { ToAddresses = new List<string> { ConfigurationManager.AppSettings["AdminEmail"] } },
                Message = new Message
                {
                    Subject = new Content("Upload Successful"),
                    Body = new Body { Text = new Content($"The upload for album {m_iAlbumId} design {iDesignId} ({m_patternInfo.Title}) pinId {pinId} was successful.") }
                }
            };
            sesClient.SendEmailAsync(emailRequest);
            txtStatus.Text = "Upload and insertion completed successfully. Starting reboot...\r\n";
        }
        private string GetPhotoKey(int iDesignId)
        {
            string photoFileName = Path.GetFileName(m_strImageFileName);
            string photoKey = $"{m_photoPrefix}/{m_iAlbumId}/{iDesignId}/{photoFileName}";
            return photoKey;
        }
        private void LoadAlbumId()
        {
            string? albumFile = Directory.GetFiles(m_strBatchFolder, "*.txt").FirstOrDefault(); ;

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
                    System.Windows.MessageBox.Show("Invalid AlbumID in .txt file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Exactly one .txt file expected for AlbumID.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if(m_patternInfo == null)
            {
                txtStatus.Text = "Extract pdf info before";
            }
            if (string.IsNullOrEmpty(m_strBatchFolder) || string.IsNullOrEmpty(txtAlbumNumber.Text))
            {
                System.Windows.MessageBox.Show("Please select a folder and ensure AlbumID is loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtStatus.Text = "Processing...";

            try
            {
                int nGlobalPage = await GetMaxGlobalPage() + 1;
                string strNPage = await GetNPage();
                string sccFile = GetSccFile();
                int iDesignId = await GetDesignId();
                
                UploadChart(iDesignId, sccFile);

                UploadPDF(iDesignId);

                UploadImage(iDesignId);

                InsertItemIntoDynamoDB(strNPage, iDesignId, nGlobalPage);

                string strPinID = await CreatePinterestPin(iDesignId);

                SendNotificationMailToAdmin(iDesignId, strPinID);

                await m_ec2Helper.RebootInstancesRequest(msg => Dispatcher.Invoke(() => txtStatus.Text += msg));
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Operation failed: {ex.Message}";
                System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<System.Drawing.Image> ExtractImages(String PDFSourcePath)
        {
            List<System.Drawing.Image> ImgList = new List<System.Drawing.Image>();

            iTextSharp.text.pdf.RandomAccessFileOrArray RAFObj = null;
            iTextSharp.text.pdf.PdfReader PDFReaderObj = null;
            iTextSharp.text.pdf.PdfObject PDFObj = null;
            iTextSharp.text.pdf.PdfStream PDFStreamObj = null;

            try
            {
                RAFObj = new iTextSharp.text.pdf.RandomAccessFileOrArray(PDFSourcePath);
                PDFReaderObj = new iTextSharp.text.pdf.PdfReader(RAFObj, null);

                for (int i = 0; i <= PDFReaderObj.XrefSize - 1; i++)
                {
                    PDFObj = PDFReaderObj.GetPdfObject(i);

                    if ((PDFObj != null) && PDFObj.IsStream())
                    {
                        PDFStreamObj = (iTextSharp.text.pdf.PdfStream)PDFObj;
                        iTextSharp.text.pdf.PdfObject subtype = PDFStreamObj.Get(iTextSharp.text.pdf.PdfName.SUBTYPE);

                        if ((subtype != null) && subtype.ToString() == iTextSharp.text.pdf.PdfName.IMAGE.ToString())
                        {
                            try
                            {

                                iTextSharp.text.pdf.parser.PdfImageObject PdfImageObj =
                         new iTextSharp.text.pdf.parser.PdfImageObject((iTextSharp.text.pdf.PRStream)PDFStreamObj);

                                System.Drawing.Image ImgPDF = PdfImageObj.GetDrawingImage();


                                ImgList.Add(ImgPDF);

                            }
                            catch (Exception)
                            {

                            }
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

        void GetImage(String strPDFFileName)
        {
            if (!File.Exists(strPDFFileName))
            {
                ShowMessage(String.Format("No file {0}", strPDFFileName));
                return;
            }

            List<System.Drawing.Image> lstImages = ExtractImages(strPDFFileName);
            if (lstImages.Count < 1)
            {
                ShowMessage("Failed to get Image");
                return;
            }

            Bitmap bitmap = new Bitmap(lstImages[0]);
            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
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
                // According to MSDN, "The default OnDemand cache option retains access to the stream until the image is needed."
                // Force the bitmap to load right now so we can dispose the stream.
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
            File.AppendAllLines("DesignToAlbumMap.csv", new string[] { "DesignID,AlbumID" });
            string prevDesignStr = "";
            string prevAlbum = "";
            SortedDictionary<int, int> designToAlbumMap = new SortedDictionary<int, int>();
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
                var designId = 0;
                if (!Int32.TryParse(designStr, out designId))
                    continue;
                var albumId = 0;
                if (!Int32.TryParse(album, out albumId))
                    continue;
                designToAlbumMap[designId] = albumId;

                prevDesignStr = designStr;
                prevAlbum = album;
            }
            foreach (var kvp in designToAlbumMap)
            {
                File.AppendAllLines("DesignToAlbumMap.csv", new string[] { $"{kvp.Key},{kvp.Value}" });
            }
        }

    }
}
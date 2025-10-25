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
        PatternInfo m_patternInfo;

        EC2Helper m_ec2Helper = new EC2Helper(RegionEndpoint.USEast1, "cross-stitch-env");
        S3Helper m_s3Helper = new S3Helper(RegionEndpoint.USEast1, "cross-stitch-designs");
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

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
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

        private void LoadAlbumId()
        {
            var txtFiles = Directory.GetFiles(m_strBatchFolder, "*.txt");
            if (txtFiles.Length == 1)
            {
                string albumFile = txtFiles[0];
                string albumIdStr = Path.GetFileNameWithoutExtension(albumFile);
                if (int.TryParse(albumIdStr, out int albumId))
                {
                    txtAlbumNumber.Text = albumId.ToString();
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
            if (string.IsNullOrEmpty(m_strBatchFolder) || string.IsNullOrEmpty(txtAlbumNumber.Text))
            {
                System.Windows.MessageBox.Show("Please select a folder and ensure AlbumID is loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtStatus.Text = "Processing...";

            try
            {
                int albumId = int.Parse(txtAlbumNumber.Text);
                string albumPartitionKey = $"ALB#{albumId.ToString("D4")}";

                // Find .scc file for DesignName
                var sccFiles = Directory.GetFiles(m_strBatchFolder, "*.scc");
                if (sccFiles.Length != 1)
                {
                    throw new Exception("Exactly one .scc file expected.");
                }
                string sccFile = sccFiles[0];
                string designName = Path.GetFileNameWithoutExtension(sccFile);

                // Find 1.pdf
                string pdfFile = Path.Combine(m_strBatchFolder, "1.pdf");
                if (!File.Exists(pdfFile))
                {
                    throw new Exception("1.pdf not found.");
                }

                // Get max DesignID
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
                    ? int.Parse(maxGlobalPageResponse.Items[0]["NGlobalPage"].N) + 1
                    : 1;

                // Get max NPage for the album
                var maxNPageRequest = new QueryRequest
                {
                    TableName = "CrossStitchItems",
                    KeyConditionExpression = "ID = :id",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":id", new AttributeValue { S = albumPartitionKey } }
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

                // Upload .scc as .css with new name
                string paddedDesignId = designId.ToString("D5");
                string cssKey = $"charts/{paddedDesignId}_{designName}.css";
                var cssUploadRequest = new TransferUtilityUploadRequest
                {
                    FilePath = sccFile,
                    BucketName = m_strBucketName,
                    Key = cssKey,
                    ContentType = "text/css"
                };
                var transferUtility = new TransferUtility(s3Client);
                await transferUtility.UploadAsync(cssUploadRequest);

                // Upload 1.pdf with new name
                string pdfKey = $"pdfs/{albumId}/Stitch{designId}_Kit.pdf";
                var pdfUploadRequest = new TransferUtilityUploadRequest
                {
                    FilePath = pdfFile,
                    BucketName = m_strBucketName,
                    Key = pdfKey,
                    ContentType = "application/pdf"
                };
                await transferUtility.UploadAsync(pdfUploadRequest);

                // Upload Image
                string photoFileName = Path.GetFileName(m_strImageFileName);
                string photoKey = $"{m_photoPrefix}/{albumId}/{designId}/{photoFileName}";
                var photoUploadRequest = new TransferUtilityUploadRequest
                {
                    FilePath = m_strImageFileName,
                    BucketName = m_strBucketName,
                    Key = photoKey,
                    ContentType = "image/jpeg"
                };
                await transferUtility.UploadAsync(photoUploadRequest);


                // Insert new item into DynamoDB
                var putItemRequest = new PutItemRequest
                {
                    TableName = "CrossStitchItems",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "ID", new AttributeValue { S = albumPartitionKey } },
                        { "NPage", new AttributeValue { S = nPage } },
                        { "AlbumID", new AttributeValue { N = albumId.ToString() } },
                        { "Caption", new AttributeValue { S = designName } },
                        { "Description", new AttributeValue { S = m_patternInfo.Description } },
                        { "DesignID", new AttributeValue { N = designId.ToString() } },
                        { "EntityType", new AttributeValue { S = "DESIGN" } },
                        { "Height", new AttributeValue { N = m_patternInfo.Height.ToString()} },
                        { "NColors", new AttributeValue { N = m_patternInfo.NColors.ToString()} },
                        { "NDownloaded", new AttributeValue { N = "0"} },
                        { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                        { "Notes", new AttributeValue { S = m_patternInfo.Notes } },
                        { "Width", new AttributeValue { N = m_patternInfo.Width.ToString()} },
                       // Add other attributes as needed
                    }
                };
                await dynamoDbClient.PutItemAsync(putItemRequest);
                string mappingKey = $"{m_photoPrefix}/mappings";
                await CreateDesignToAlbumMap(s3Client, m_strBucketName, mappingKey);
                // Send notification email to admin
                var emailRequest = new SendEmailRequest
                {
                    Source = ConfigurationManager.AppSettings["SenderEmail"],
                    Destination = new Destination { ToAddresses = new List<string> { ConfigurationManager.AppSettings["AdminEmail"] } },
                    Message = new Message
                    {
                        Subject = new Content("Upload Successful"),
                        Body = new Body { Text = new Content($"The upload for album {albumId} design {designId} ({designName}) was successful.") }
                    }
                };
                await m_s3Helper.DeleteFileAsync("mappings/design-album-mapping.csv");
                await sesClient.SendEmailAsync(emailRequest);
                txtStatus.Text = "Upload and insertion completed successfully.";
                await m_ec2Helper.RebootInstancesRequest();
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
            iTextSharp.text.pdf.PdfStream PDFStremObj = null;

            try
            {
                RAFObj = new iTextSharp.text.pdf.RandomAccessFileOrArray(PDFSourcePath);
                PDFReaderObj = new iTextSharp.text.pdf.PdfReader(RAFObj, null);

                for (int i = 0; i <= PDFReaderObj.XrefSize - 1; i++)
                {
                    PDFObj = PDFReaderObj.GetPdfObject(i);

                    if ((PDFObj != null) && PDFObj.IsStream())
                    {
                        PDFStremObj = (iTextSharp.text.pdf.PdfStream)PDFObj;
                        iTextSharp.text.pdf.PdfObject subtype = PDFStremObj.Get(iTextSharp.text.pdf.PdfName.SUBTYPE);

                        if ((subtype != null) && subtype.ToString() == iTextSharp.text.pdf.PdfName.IMAGE.ToString())
                        {
                            try
                            {

                                iTextSharp.text.pdf.parser.PdfImageObject PdfImageObj =
                         new iTextSharp.text.pdf.parser.PdfImageObject((iTextSharp.text.pdf.PRStream)PDFStremObj);

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
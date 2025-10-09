using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Transfer;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Uploader
{
    public partial class MainWindow : Window
    {
        private string selectedFolder = string.Empty;
        private readonly string bucketName = ConfigurationManager.AppSettings["S3Bucket"] ?? "cross-stitch-designs";
        private readonly AmazonDynamoDBClient dynamoDbClient = new AmazonDynamoDBClient();
        private readonly AmazonS3Client s3Client = new AmazonS3Client();

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
                    selectedFolder = dialog.SelectedPath;
                    txtFolderPath.Text = selectedFolder;
                    LoadAlbumId();
                }
            }
        }

        private void LoadAlbumId()
        {
            var txtFiles = Directory.GetFiles(selectedFolder, "*.txt");
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
            if (string.IsNullOrEmpty(selectedFolder) || string.IsNullOrEmpty(txtAlbumNumber.Text))
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
                var sccFiles = Directory.GetFiles(selectedFolder, "*.scc");
                if (sccFiles.Length != 1)
                {
                    throw new Exception("Exactly one .scc file expected.");
                }
                string sccFile = sccFiles[0];
                string designName = Path.GetFileNameWithoutExtension(sccFile);

                // Find 1.pdf
                string pdfFile = Path.Combine(selectedFolder, "1.pdf");
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
                string nPage = newNPageNum.ToString("D5"); // Pad with leading zeros to 7 characters

                // Upload .scc as .css with new name
                string paddedDesignId = designId.ToString("D5");
                string cssKey = $"charts/{paddedDesignId}_{designName}.css";
                var cssUploadRequest = new TransferUtilityUploadRequest
                {
                    FilePath = sccFile,
                    BucketName = bucketName,
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
                    BucketName = bucketName,
                    Key = pdfKey,
                    ContentType = "application/pdf"
                };
                await transferUtility.UploadAsync(pdfUploadRequest);

                // Insert new item into DynamoDB
                var putItemRequest = new PutItemRequest
                {
                    TableName = "CrossStitchItems",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "ID", new AttributeValue { S = albumPartitionKey } },
                        { "NPage", new AttributeValue { S = nPage } },
                        { "EntityType", new AttributeValue { S = "DESIGN" } },
                        { "DesignID", new AttributeValue { N = designId.ToString() } },
                        { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                        { "DesignName", new AttributeValue { S = designName } },
                        { "AlbumID", new AttributeValue { N = albumId.ToString() } }
                        // Add other attributes as needed
                    }
                };
                await dynamoDbClient.PutItemAsync(putItemRequest);

                txtStatus.Text = "Upload and insertion completed successfully.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Operation failed: {ex.Message}";
                System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
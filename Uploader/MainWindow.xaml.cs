using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using System.Windows.Controls;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using UploadPatterns;
using Uploader.Helpers;
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
    /// - Sends notification email, reboots EC2 environment, and notifies users
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
        private readonly PatternLinkHelper _linkHelper = new PatternLinkHelper();
        private readonly EmailHelper _emailHelper = new EmailHelper();
        private readonly TransferUtility _s3TransferUtility;

        private string _imageFilePath = string.Empty;
        private string _batchFolderPath = string.Empty;
        private bool _isSendingEmails;
        private bool _isSendingTextEmails;

        private const string TextEmailSubjectDefault = "❌🪡❌🪡❌ New website address 🧵";
        private const string TextEmailBodyDefault =
            "Hello <username>,\r\n" +
            "I'm writing to let you know that my site has moved to a new address: https://cross-stitch.com\r\n" +
            "If you have a bookmark saved, please update it.\r\n\r\n" +
            "Everything works the same as before.\r\n" +
            "Warm regards,\r\n" +
            "Ann";
        private const string PhotoPrefix = "photos";
        private const string UserEmailSubject = "❌🪡❌🪡❌ Blue Bolt Buddy PDF is ready! 🔵";
        private const string SuppressedListPath = @"D:\ann\Git\cross-stitch\list-suppressed.txt";
        private const string ConverterExePath = @"D:\ann\Git\Converter\bin\Release\net9.0\Converter.exe";
        private static readonly string[] RequiredPdfVariants = { "1", "3", "5" };
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
                var requiredPdfs = new[] { "1.pdf", "3.pdf", "5.pdf" };
                var missing = requiredPdfs
                    .Where(name => !File.Exists(Path.Combine(_batchFolderPath, name)))
                    .ToList();
                if (missing.Count > 0)
                {
                    string missingList = string.Join(", ", missing);
                    txtStatus.Text = $"Missing required PDFs: {missingList}\r\n";
                    MessageBox.Show($"Missing required PDFs: {missingList}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

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
                await RunFullUploadFlowAsync();

                // Continuation is on UI thread (no ConfigureAwait(false) here)
                txtStatus.Text += "[Upload] Done. Use Send Emails when you're ready to notify.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"[Upload] Operation failed: {ex.Message}\r\n";
                txtStatus.Text += $"[Upload] Exception details: {ex}\r\n";

                MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnSendEmails_Click(object sender, RoutedEventArgs e)
        {
            if (PatternInfo == null)
            {
                txtStatus.Text += "[Email] Upload a pattern before sending emails.\r\n";
                return;
            }

            if (string.IsNullOrWhiteSpace(PatternInfo.PinID))
            {
                txtStatus.Text += "[Email] Pinterest pin is missing; complete upload first.\r\n";
                return;
            }

            if (_isSendingEmails)
            {
                txtStatus.Text += "[Email] Send already in progress.\r\n";
                return;
            }

            _isSendingEmails = true;
            var sendButton = sender as System.Windows.Controls.Button;
            if (sendButton != null)
                sendButton.IsEnabled = false;

            txtStatus.Text += "[Email] Sending notification emails...\r\n";

            try
            {
                await SendNotificationEmailsAsync().ConfigureAwait(false);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += "[Email] Sent notification emails to admin and users.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"[Email] Failed to send notification emails: {ex.Message}\r\n";
                }));

                MessageBox.Show($"Failed to send emails: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSendingEmails = false;
                if (sendButton != null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        sendButton.IsEnabled = true;
                    }));
                }
            }
        }

        private async void BtnSendTextEmails_Click(object sender, RoutedEventArgs e)
        {
            if (_isSendingTextEmails)
            {
                txtStatus.Text += "[Email/Text] Send already in progress.\r\n";
                return;
            }

            _isSendingTextEmails = true;
            var sendButton = sender as System.Windows.Controls.Button;
            if (sendButton != null)
                sendButton.IsEnabled = false;

            txtStatus.Text += "[Email/Text] Sending text-only emails...\r\n";

            try
            {
                await SendTextOnlyEmailsAsync().ConfigureAwait(false);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += "[Email/Text] Sent text-only emails to admin and users.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"[Email/Text] Failed to send text-only emails: {ex.Message}\r\n";
                }));

                MessageBox.Show($"Failed to send text-only emails: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSendingTextEmails = false;
                if (sendButton != null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        sendButton.IsEnabled = true;
                    }));
                }
            }
        }

        private async void BtnTestPinterest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Hard-coded test path - adjust if needed
                var info = new PatternInfo(@"D:\Stitch Craft\Charts\ReadyCharts\2025_11_02\1.pdf");
                string pinId = await _pinterestHelper.UploadPinForPatternAsync(info, true);

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
            IProgress<string> progress = new Progress<string>(msg =>
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

        private async void InitializeUserUnsubscribe_Click(object sender, RoutedEventArgs e)
        {

            txtStatus.Text = "Initializing user unsubscribe fields...\r\n";
            try
            {
                await InitializeUserUnsubscribeFieldsAsync();
                // Back on UI thread
                txtStatus.Text += "Finished initializing user unsubscribe fields.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void InitializeUserCid_Click(object sender, RoutedEventArgs e)
        {

            txtStatus.Text = "Initializing user cid fields...\r\n";
            try
            {
                await InitializeUserCidFieldsAsync();
                // Back on UI thread
                txtStatus.Text += "Finished initializing user cid fields.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CheckMissingPdfs_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text += "Checking S3 for missing PDFs...\r\n";

            IProgress<string> progress = new Progress<string>(msg =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += msg + Environment.NewLine;
                }));
            });

            try
            {
                var designs = await LoadAllDesignLocationsAsync(progress).ConfigureAwait(false);
                progress.Report($"Fetched {designs.Count} designs from DynamoDB.");

                var pdfKeys = await LoadAllPdfKeysAsync(progress).ConfigureAwait(false);

                var missing = FindDesignsWithMissingPdfs(designs, pdfKeys);
                string reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MissingDesignPdfs.txt");
                await WriteMissingPdfReportAsync(reportPath, missing).ConfigureAwait(false);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Missing PDFs for {missing.Count} design(s). Report written to: {reportPath}\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to check PDFs: {ex.Message}\r\n";
                }));
            }
        }

        private void TxtStatus_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            txtStatus.ScrollToEnd();
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
            if (patternInfo.AlbumId == 0)
            {
                throw new Exception("Failed to load AlbumID from .txt file.");
            }
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
        private async Task RunFullUploadFlowAsync()
        {
            // 1. Calculate global page and next design ID
            int maxGlobalPage = await GetMaxGlobalPageAsync();
            int nGlobalPage = maxGlobalPage + 1;

            string sccFile = GetSccFile();

            // Recalculate DesignID to avoid conflicts
            PatternInfo.DesignID = await GetNextDesignIdAsync().ConfigureAwait(false);

            // 2. Upload files to S3
            await UploadChartToS3Async(PatternInfo.DesignID, sccFile).ConfigureAwait(false);
            await UploadPdfToS3Async(PatternInfo.DesignID).ConfigureAwait(false);
            await UploadImageToS3Async(PatternInfo.DesignID).ConfigureAwait(false);

            // 3. Create Pinterest pin
            PatternInfo.PinID = await _pinterestHelper.UploadPinForPatternAsync(PatternInfo).ConfigureAwait(false);

            // 4. Insert item into DynamoDB
            await InsertItemIntoDynamoDbAsync(nGlobalPage).ConfigureAwait(false);

            // 5. Reboot EC2 environment (status text is updated via callback which marshals to UI)
            bool rebooted = await _ec2Helper.RebootInstancesRequest(msg =>
            {
                Dispatcher.BeginInvoke(new Action(() => { txtStatus.Text += msg; }));
            }).ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += rebooted
                    ? "Reboot requested successfully.\r\n"
                    : "Reboot failed.\r\n";
            }));
        }

        private async Task SendNotificationEmailsAsync()
        {
            if (PatternInfo == null)
                throw new InvalidOperationException("PatternInfo must be set before sending emails.");

            var albumSuggestions = await FetchAlbumSuggestionsAsync(4).ConfigureAwait(false);

            await SendNotificationMailToAdminAsync(PatternInfo.DesignID, PatternInfo.PinID, albumSuggestions)
                .ConfigureAwait(false);

            var userRecipients = await FetchAllUserEmailsAsync(onlyVerified: true, onlySubscribed: true).ConfigureAwait(false);
            await SendNotificationMailToUsersAsync(
                    PatternInfo.DesignID,
                    PatternInfo.PinID,
                    userRecipients,
                    albumSuggestions)
                .ConfigureAwait(false);
        }

        private async Task SendTextOnlyEmailsAsync()
        {
            string? sender = ConfigurationManager.AppSettings["SenderEmail"];
            string? admin = ConfigurationManager.AppSettings["AdminEmail"];
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";
            string emailAttribute = ConfigurationManager.AppSettings["UserEmailAttribute"] ?? "Email";
            string userIdAttribute = ConfigurationManager.AppSettings["UserIdAttribute"] ?? "ID";
            string verifiedAttribute = ConfigurationManager.AppSettings["UserVerifiedAttribute"] ?? "Verified";
            string unsubscribedAttribute = ConfigurationManager.AppSettings["UserUnsubscribedAttribute"] ?? "Unsubscribed";

            string subject = ConfigurationManager.AppSettings["TextEmailSubject"] ?? TextEmailSubjectDefault;
            string baseTextBody = ConfigurationManager.AppSettings["TextEmailBody"] ?? TextEmailBodyDefault;
            string baseHtmlBody = ConvertPlainTextToHtml(baseTextBody);

            if (string.IsNullOrWhiteSpace(sender))
                throw new InvalidOperationException("SenderEmail is not configured.");
            if (string.IsNullOrWhiteSpace(subject))
                throw new InvalidOperationException("Text email subject is empty.");
            if (string.IsNullOrWhiteSpace(baseTextBody))
                throw new InvalidOperationException("Text email body is empty.");

            var userRecipients = await FetchAllUserEmailsAsync(onlyVerified: true, onlySubscribed: true).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(admin))
            {
                string adminUnsubUrl = BuildUnsubscribeUrl(admin);
                var adminHeaders = BuildUnsubscribeHeaders(adminUnsubUrl, sender);
                string adminBaseText = PersonalizeTextTemplate(baseTextBody, null);
                string adminBaseHtml = PersonalizeHtmlTemplate(baseHtmlBody, null);
                string adminTextBody = adminBaseText + $"\r\n\r\nUnsubscribe: {adminUnsubUrl}";
                string? adminHtmlBody = string.IsNullOrWhiteSpace(adminBaseHtml)
                    ? null
                    : adminBaseHtml + $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{adminUnsubUrl}\">unsubscribe</a>.</p>";

                await _emailHelper.SendEmailAsync(
                    _sesClient,
                    sender,
                    new[] { admin },
                    subject,
                    adminTextBody,
                    adminHtmlBody,
                    adminHeaders).ConfigureAwait(false);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += "[TextEmail] Sent text-only email to admin.\r\n";
                }));
            }

            var recipients = userRecipients;
           
            if (!string.IsNullOrWhiteSpace(admin))
            {
                recipients = userRecipients
                    .Where(r => !string.Equals(r.Email, admin, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (recipients.Count == 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += "[TextEmail] No user recipients found.\r\n";
                }));
                return;
            }

            int totalUserCount = await CountUsersAsync(
                    usersTable,
                    $"{verifiedAttribute} = :trueVal AND (attribute_not_exists({unsubscribedAttribute}) OR {unsubscribedAttribute} = :falseVal)",
                    new Dictionary<string, AttributeValue>
                    {
                        { ":trueVal", new AttributeValue { BOOL = true } },
                        { ":falseVal", new AttributeValue { BOOL = false } }
                    })
                .ConfigureAwait(false);

            await SendTextEmailsWithProgressAsync(
                "[TextEmail]",
                recipients,
                subject,
                sender,
                baseTextBody,
                baseHtmlBody,
                totalUserCount,
                usersTable,
                emailAttribute,
                userIdAttribute).ConfigureAwait(false);
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
            string pdf1Path = Path.Combine(_batchFolderPath, "1.pdf");
            string pdf3Path = Path.Combine(_batchFolderPath, "3.pdf");
            string pdf5Path = Path.Combine(_batchFolderPath, "5.pdf");

            if (!File.Exists(pdf1Path) || !File.Exists(pdf3Path) || !File.Exists(pdf5Path))
            {
                throw new Exception("Required PDFs (1.pdf, 3.pdf, 5.pdf) not found.");
            }

            string mainKey = $"pdfs/{_albumId}/Stitch{designId}_Kit.pdf";
            string designFolder = $"pdfs/{_albumId}/{designId}";
            string key1 = $"{designFolder}/Stitch{designId}_1_Kit.pdf";
            string key3 = $"{designFolder}/Stitch{designId}_3_Kit.pdf";
            string key5 = $"{designFolder}/Stitch{designId}_5_Kit.pdf";

            string convertedPdf1Path = await ConvertPdfForUploadAsync(pdf1Path).ConfigureAwait(false);
            string convertedPdf3Path = await ConvertPdfForUploadAsync(pdf3Path).ConfigureAwait(false);
            string convertedPdf5Path = await ConvertPdfForUploadAsync(pdf5Path).ConfigureAwait(false);

            await UploadPdfFileAsync(convertedPdf1Path, mainKey).ConfigureAwait(false);
            await UploadPdfFileAsync(convertedPdf1Path, key1).ConfigureAwait(false);
            await UploadPdfFileAsync(convertedPdf3Path, key3).ConfigureAwait(false);
            await UploadPdfFileAsync(convertedPdf5Path, key5).ConfigureAwait(false);
        }

        private static async Task<string> ConvertPdfForUploadAsync(string inputPath)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Input PDF not found.", inputPath);

            if (!File.Exists(ConverterExePath))
                throw new FileNotFoundException("Converter.exe not found.", ConverterExePath);

            string? folder = Path.GetDirectoryName(inputPath);
            string outputPath = Path.Combine(folder ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(inputPath)}.converted.pdf");

            var startInfo = new ProcessStartInfo
            {
                FileName = ConverterExePath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(inputPath);

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start PDF converter process.");

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                throw new Exception(
                    $"Converter failed for {Path.GetFileName(inputPath)} (exit {process.ExitCode}). {details}".Trim());
            }

            if (!File.Exists(outputPath))
                throw new Exception($"Converter did not produce expected output: {outputPath}");

            return outputPath;
        }

        private Task UploadPdfFileAsync(string filePath, string key)
        {
            var request = new TransferUtilityUploadRequest
            {
                FilePath = filePath,
                BucketName = _bucketName,
                Key = key,
                ContentType = "application/pdf"
            };

            return _s3TransferUtility.UploadAsync(request);
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

        private async Task InsertItemIntoDynamoDbAsync(int nGlobalPage)
        {
            if (PatternInfo == null)
                throw new InvalidOperationException("PatternInfo is not initialized.");

            var item = new Dictionary<string, AttributeValue>
            {
                { "ID",          new AttributeValue { S = AlbumPartitionKey } },
                { "NPage",       new AttributeValue { S = PatternInfo.NPage } },
                { "AlbumID",     new AttributeValue { N = _albumId.ToString() } },
                { "Caption",     new AttributeValue { S = PatternInfo.Title } },
                { "Description", new AttributeValue { S = PatternInfo.Description } },
                { "DesignID",    new AttributeValue { N = PatternInfo.DesignID.ToString() } },
                { "EntityType",  new AttributeValue { S = "DESIGN" } },
                { "Height",      new AttributeValue { N = PatternInfo.Height.ToString() } },
                { "NColors",     new AttributeValue { N = PatternInfo.NColors.ToString() } },
                { "NDownloaded", new AttributeValue { N = "0" } },
                { "NGlobalPage", new AttributeValue { N = nGlobalPage.ToString() } },
                { "Notes",       new AttributeValue { S = PatternInfo.Notes } },
                { "Width",       new AttributeValue { N = PatternInfo.Width.ToString() } },
                { "PinID",       new AttributeValue { S = PatternInfo.PinID ?? string.Empty } }
            };

            var request = new PutItemRequest
            {
                TableName = "CrossStitchItems",
                Item = item
            };

            await _dynamoDbClient.PutItemAsync(request).ConfigureAwait(false);
        }

        private async Task<List<AlbumInfo>> FetchAlbumSuggestionsAsync(int takeCount)
        {
            var albums = new List<AlbumInfo>();
            string tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";

            try
            {
                var scanRequest = new ScanRequest
                {
                    TableName = tableName,
                    FilterExpression = "EntityType = :albumType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":albumType", new AttributeValue { S = "ALBUM" } }
                    },
                    ProjectionExpression = "ID, Caption, EntityType"
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);
                    lastEvaluatedKey = response.LastEvaluatedKey;

                    foreach (var item in response.Items)
                    {
                        if (!item.TryGetValue("ID", out var idAttr) || string.IsNullOrEmpty(idAttr.S))
                            continue;

                        string id = idAttr.S;
                        if (!id.StartsWith("ALB#", StringComparison.OrdinalIgnoreCase) || id.Length <= 4)
                            continue;

                        string albumId = id.Substring(4);
                        string caption = item.TryGetValue("Caption", out var captionAttr)
                            ? captionAttr.S ?? string.Empty
                            : string.Empty;

                        albums.Add(new AlbumInfo
                        {
                            AlbumId = albumId,
                            Caption = caption
                        });
                    }
                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to fetch albums: {ex.Message}\r\n";
                }));
                return new List<AlbumInfo>();
            }

            string currentAlbum = _albumId.ToString("D4");
            var pool = albums
                .Where(a => !string.Equals(a.AlbumId, currentAlbum, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pool.Count == 0)
                pool = albums;

            return TakeRandomAlbums(pool, takeCount);
        }

        private static List<AlbumInfo> TakeRandomAlbums(List<AlbumInfo> source, int takeCount)
        {
            if (source.Count == 0 || takeCount <= 0)
                return new List<AlbumInfo>();

            if (source.Count <= takeCount)
                return source.Take(takeCount).ToList();

            var rng = new Random();
            for (int i = source.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (source[i], source[j]) = (source[j], source[i]);
            }

            return source.Take(takeCount).ToList();
        }

        private string BuildAlbumSuggestionsHtml(IReadOnlyList<AlbumInfo> albums, string? cid = null, string? eid = null)
        {
            if (albums == null || albums.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("<p>Explore more albums:</p><ul>");
            int index = 1;

            foreach (var album in albums)
            {
                string caption = string.IsNullOrWhiteSpace(album.Caption)
                    ? $"Featured album {index}"
                    : album.Caption;
                string url = _linkHelper.BuildAlbumUrl(album.AlbumId, album.Caption);
                url = AppendTrackingParameters(url, cid, eid);

                sb.Append($"<li><a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(caption)}</a></li>");
                index++;
            }

            sb.Append("</ul>");
            return sb.ToString();
        }

        private string BuildAlbumSuggestionsText(IReadOnlyList<AlbumInfo> albums, string? cid = null, string? eid = null)
        {
            if (albums == null || albums.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Explore more albums:");
            int index = 1;

            foreach (var album in albums)
            {
                string caption = string.IsNullOrWhiteSpace(album.Caption)
                    ? $"Featured album {index}"
                    : album.Caption;
                string url = _linkHelper.BuildAlbumUrl(album.AlbumId, album.Caption);
                url = AppendTrackingParameters(url, cid, eid);

                sb.AppendLine($"- {caption}: {url}");
                index++;
            }

            return sb.ToString();
        }

        private async Task SendNotificationMailToAdminAsync(
            int designId,
            string pinId,
            IReadOnlyList<AlbumInfo> albumSuggestions)
        {
            string? sender = ConfigurationManager.AppSettings["SenderEmail"];
            string? admin = ConfigurationManager.AppSettings["AdminEmail"];

            if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(admin) || PatternInfo == null)
                return;

            string patternUrl = _linkHelper.BuildPatternUrl(PatternInfo);
            string imageUrl = _linkHelper.BuildImageUrl(designId, _albumId);
            string patternUrlWithUtm = AppendUtmParameters(patternUrl);
            string altText = string.IsNullOrWhiteSpace(PatternInfo.Title)
                ? "New cross stitch pattern"
                : PatternInfo.Title;
            string unsubscribeUrl = BuildUnsubscribeUrl(admin);
            var unsubscribeHeaders = BuildUnsubscribeHeaders(unsubscribeUrl, sender);
            string albumHtml = BuildAlbumSuggestionsHtml(albumSuggestions);
            string albumText = BuildAlbumSuggestionsText(albumSuggestions);

            string htmlBody =
                $"<p>The upload for album {_albumId} design {designId} was successful.</p>" +
                $"<p><a href=\"{patternUrlWithUtm}\">" +
                $"<img src=\"{imageUrl}\" alt=\"{altText}\" style=\"max-width:280px; max-height:280px; width:auto; height:auto; border:0;\"/>" +
                $"</a></p>" +
                $"<p>Pin ID: {pinId}</p>" +
                albumHtml +
                $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{unsubscribeUrl}\">unsubscribe</a>.</p>";

            string textBody =
                $"The upload for album {_albumId} design {designId} ({PatternInfo.Title}) pinId {pinId} was successful."
                + albumText
                + $"\r\nUnsubscribe: {unsubscribeUrl}";

            await _emailHelper.SendEmailAsync(
                _sesClient,
                sender,
                new[] { admin },
                "Upload Successful",
                textBody,
                htmlBody,
                unsubscribeHeaders).ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += "Sent notification email to admin.\r\n";
            }));
        }

        /// <summary>
        /// One-time (or repeatable) migration helper:
        /// for every user in the CrossStitchUsers table, ensures two attributes exist:
        /// - UnsubscribeToken (string, securely generated if missing)
        /// - Unsubscribed   (bool, false by default if missing)
        ///
        /// Existing values are preserved; only missing attributes are added.
        /// </summary>
        private async Task InitializeUserUnsubscribeFieldsAsync()
        {
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";

            int updatedCount = 0;
            int skippedCount = 0;

            try
            {
                var scanRequest = new ScanRequest
                {
                    TableName = usersTable
                    // No ProjectionExpression: we read full items to keep things simple
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        if (!item.TryGetValue("ID", out var idAttr))
                        {
                            // Without PK we cannot update this record safely.
                            continue;
                        }

                        bool hasToken =
                            item.TryGetValue("UnsubscribeToken", out var tokenAttr) &&
                            !string.IsNullOrWhiteSpace(tokenAttr.S);

                        bool hasUnsubscribed = item.ContainsKey("Unsubscribed");

                        if (hasToken && hasUnsubscribed)
                        {
                            skippedCount++;
                            continue;
                        }

                        var key = new Dictionary<string, AttributeValue>
                        {
                            { "ID", idAttr }
                        };

                        var exprValues = new Dictionary<string, AttributeValue>();
                        var setClauses = new List<string>();

                        if (!hasToken)
                        {
                            exprValues[":token"] = new AttributeValue
                            {
                                S = GenerateRandomToken()
                            };
                            setClauses.Add("UnsubscribeToken = :token");
                        }

                        if (!hasUnsubscribed)
                        {
                            exprValues[":falseVal"] = new AttributeValue
                            {
                                BOOL = false
                            };
                            setClauses.Add("Unsubscribed = :falseVal");
                        }

                        if (setClauses.Count == 0)
                        {
                            skippedCount++;
                            continue;
                        }

                        string updateExpression = "SET " + string.Join(", ", setClauses);

                        var updateRequest = new UpdateItemRequest
                        {
                            TableName = usersTable,
                            Key = key,
                            UpdateExpression = updateExpression,
                            ExpressionAttributeValues = exprValues
                        };

                        await _dynamoDbClient.UpdateItemAsync(updateRequest).ConfigureAwait(false);
                        updatedCount++;
                    }

                    lastEvaluatedKey = response.LastEvaluatedKey;
                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text +=
                        $"InitializeUserUnsubscribeFieldsAsync finished. Updated {updatedCount} user(s), skipped {skippedCount} user(s).\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to initialize unsubscribe fields for users: {ex.Message}\r\n";
                }));
            }
        }

        /// <summary>
        /// Counts users in a table with an optional filter.
        /// </summary>
        private async Task<int> CountUsersAsync(
            string tableName,
            string? filterExpression = null,
            Dictionary<string, AttributeValue>? expressionValues = null)
        {
            var request = new ScanRequest
            {
                TableName = tableName,
                Select = Select.COUNT
            };

            if (!string.IsNullOrWhiteSpace(filterExpression))
            {
                request.FilterExpression = filterExpression;
                request.ExpressionAttributeValues = expressionValues;
            }

            int total = 0;
            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            do
            {
                request.ExclusiveStartKey = lastEvaluatedKey;
                var response = await _dynamoDbClient.ScanAsync(request).ConfigureAwait(false);
                total += response.Count;
                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            return total;
        }

        /// <summary>
        /// Adds a per-user correlation id ("cid") if missing, using a random GUID.
        /// Existing cid values are preserved.
        /// </summary>
        private async Task InitializeUserCidFieldsAsync()
        {
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";

            int updatedCount = 0;
            int skippedCount = 0;
            int missingNPageCount = 0;
            int scannedCount = 0;
            int totalCount = await CountUsersAsync(usersTable).ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (totalCount > 0)
                    txtStatus.Text += $"[CrossStitchUsers] Total users found: {totalCount}.\r\n";
                else
                    txtStatus.Text += "[CrossStitchUsers] Could not determine total users (count returned 0).\r\n";
            }));

            try
            {
                var scanRequest = new ScanRequest
                {
                    TableName = usersTable,
                    ProjectionExpression = "ID, NPage, cid"
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        scannedCount++;

                        if (!item.TryGetValue("ID", out var idAttr))
                        {
                            continue;
                        }

                        if (!item.TryGetValue("NPage", out var nPageAttr) ||
                            string.IsNullOrWhiteSpace(nPageAttr.S))
                        {
                            missingNPageCount++;
                            continue;
                        }

                        bool hasCid =
                            item.TryGetValue("cid", out var cidAttr) &&
                            !string.IsNullOrWhiteSpace(cidAttr.S);

                        if (hasCid)
                        {
                            skippedCount++;
                            continue;
                        }

                        var updateRequest = new UpdateItemRequest
                        {
                            TableName = usersTable,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { "ID", idAttr },
                                { "NPage", nPageAttr }
                            },
                            UpdateExpression = "SET cid = :cid",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                { ":cid", new AttributeValue { S = Guid.NewGuid().ToString("N") } }
                            }
                        };

                        await _dynamoDbClient.UpdateItemAsync(updateRequest).ConfigureAwait(false);
                        updatedCount++;

                        if ((updatedCount + skippedCount) % 50 == 0)
                        {
                            int progressUpdated = updatedCount;
                            int progressSkipped = skippedCount;
                            int progressScanned = scannedCount;
                            int progressMissing = missingNPageCount;
                            int remaining = totalCount > 0
                                ? Math.Max(totalCount - (progressUpdated + progressSkipped + progressMissing), 0)
                                : -1;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                string remainingText = remaining >= 0
                                    ? $"Remaining ~{remaining}"
                                    : "Remaining: unknown";
                                txtStatus.Text +=
                                    $"[CrossStitchUsers] Scanned {progressScanned}, updated {progressUpdated}, skipped {progressSkipped}, missing NPage {progressMissing}. {remainingText}.\r\n";
                            }));
                        }
                    }

                    lastEvaluatedKey = response.LastEvaluatedKey;
                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text +=
                        $"InitializeUserCidFieldsAsync finished. Total {totalCount}, updated {updatedCount} user(s), skipped {skippedCount} user(s), missing NPage {missingNPageCount}.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to initialize cid fields for users: {ex.Message}\r\n";
                }));
            }
        }

        /// <summary>
        /// Adds cid for users stored in CrossStitchItems table (ID starts with USR#).
        /// Progress is reported to the status control during processing.
        /// </summary>
        private async Task InitializeItemsUserCidFieldsAsync()
        {
            string tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";

            int updatedCount = 0;
            int skippedCount = 0;
            int scannedCount = 0;
            int missingNPageCount = 0;
            var filterValues = new Dictionary<string, AttributeValue>
            {
                { ":userPrefix", new AttributeValue { S = "USR#" } }
            };
            int totalCount = await CountUsersAsync(
                    tableName,
                    "begins_with(ID, :userPrefix)",
                    filterValues)
                .ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (totalCount > 0)
                    txtStatus.Text += $"[CrossStitchItems] Total users found: {totalCount}.\r\n";
                else
                    txtStatus.Text += "[CrossStitchItems] Could not determine total users (count returned 0).\r\n";
            }));

            try
            {
                var scanRequest = new ScanRequest
                {
                    TableName = tableName,
                    FilterExpression = "begins_with(ID, :userPrefix)",
                    ExpressionAttributeValues = filterValues,
                    ProjectionExpression = "ID, NPage, cid"
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        scannedCount++;

                        if (!item.TryGetValue("ID", out var idAttr) || string.IsNullOrWhiteSpace(idAttr.S))
                        {
                            continue;
                        }

                        if (!item.TryGetValue("NPage", out var nPageAttr) ||
                            string.IsNullOrWhiteSpace(nPageAttr.S))
                        {
                            missingNPageCount++;
                            continue;
                        }

                        bool hasCid =
                            item.TryGetValue("cid", out var cidAttr) &&
                            !string.IsNullOrWhiteSpace(cidAttr.S);

                        if (hasCid)
                        {
                            skippedCount++;
                            continue;
                        }

                        var updateRequest = new UpdateItemRequest
                        {
                            TableName = tableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { "ID", idAttr },
                                { "NPage", nPageAttr }
                            },
                            UpdateExpression = "SET cid = :cid",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                { ":cid", new AttributeValue { S = Guid.NewGuid().ToString("N") } }
                            }
                        };

                        await _dynamoDbClient.UpdateItemAsync(updateRequest).ConfigureAwait(false);
                        updatedCount++;

                        if ((updatedCount + skippedCount) % 50 == 0)
                        {
                            int progressUpdated = updatedCount;
                            int progressSkipped = skippedCount;
                            int progressScanned = scannedCount;
                            int progressMissing = missingNPageCount;
                            int remaining = totalCount > 0
                                ? Math.Max(totalCount - (progressUpdated + progressSkipped + progressMissing), 0)
                                : -1;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                string remainingText = remaining >= 0
                                    ? $"Remaining ~{remaining}"
                                    : "Remaining: unknown";
                                txtStatus.Text +=
                                    $"[CrossStitchItems] Scanned {progressScanned}, updated {progressUpdated}, skipped {progressSkipped}, missing NPage {progressMissing}. {remainingText}.\r\n";
                            }));
                        }
                    }

                    lastEvaluatedKey = response.LastEvaluatedKey;
                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text +=
                        $"InitializeItemsUserCidFieldsAsync finished. Total {totalCount}, updated {updatedCount} user(s), skipped {skippedCount} user(s), missing NPage {missingNPageCount}.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to initialize cid fields for CrossStitchItems users: {ex.Message}\r\n";
                }));
            }
        }

        private sealed class UserRecipient
        {
            public UserRecipient(string email, string? firstName, AttributeValue? idAttribute = null, string? cid = null)
            {
                Email = email;
                FirstName = firstName;
                IdAttribute = idAttribute;
                Cid = cid;
            }

            public string Email { get; }
            public string? FirstName { get; }
            public AttributeValue? IdAttribute { get; }
            public string? Cid { get; }
        }

        private async Task<List<UserRecipient>> FetchAllUserEmailsAsync(bool onlyVerified = false, bool onlySubscribed = false)
        {
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";
            string emailAttribute = ConfigurationManager.AppSettings["UserEmailAttribute"] ?? "Email";
            string firstNameAttribute = ConfigurationManager.AppSettings["UserFirstNameAttribute"] ?? "FirstName";
            string userIdAttribute = ConfigurationManager.AppSettings["UserIdAttribute"] ?? "ID";
            string userCidAttribute = ConfigurationManager.AppSettings["UserCidAttribute"] ?? "cid";
            string verifiedAttribute = ConfigurationManager.AppSettings["UserVerifiedAttribute"] ?? "Verified";
            string unsubscribedAttribute = ConfigurationManager.AppSettings["UserUnsubscribedAttribute"] ?? "Unsubscribed";

            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recipients = new List<UserRecipient>();

            try
            {
                var projectionParts = new List<string>
                {
                    emailAttribute,
                    firstNameAttribute,
                    userIdAttribute,
                    userCidAttribute
                };

                if (onlyVerified)
                    projectionParts.Add(verifiedAttribute);
                if (onlySubscribed)
                    projectionParts.Add(unsubscribedAttribute);

                var scanRequest = new ScanRequest
                {
                    TableName = usersTable,
                    ProjectionExpression = string.Join(", ", projectionParts.Distinct())
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                int nSendingLimit = 220;
                int iSent = 0;
                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        if (!item.TryGetValue(emailAttribute, out var emailAttr))
                            continue;

                        string? email = null;
                        if (!string.IsNullOrWhiteSpace(emailAttr.S))
                        {
                            email = emailAttr.S.Trim();
                        }
                        else if (emailAttr.L != null && emailAttr.L.Count > 0)
                        {
                            foreach (var entry in emailAttr.L)
                            {
                                if (!string.IsNullOrWhiteSpace(entry.S))
                                {
                                    email = entry.S.Trim();
                                }
                            }
                        } 

                        if (string.IsNullOrWhiteSpace(email))
                            continue;

                        if (!emails.Add(email))
                            continue;

                        string? firstName = null;
                        if (item.TryGetValue(firstNameAttribute, out var firstNameAttr))
                        {
                            if (!string.IsNullOrWhiteSpace(firstNameAttr.S))
                            {
                                firstName = firstNameAttr.S.Trim();
                            }
                            else if (firstNameAttr.L != null && firstNameAttr.L.Count > 0)
                            {
                                firstName = firstNameAttr.L
                                    .Select(entry => entry.S)
                                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                                    ?.Trim();
                            }
                        }

                        AttributeValue? idAttr = null;
                        if (item.TryGetValue(userIdAttribute, out var idValue))
                        {
                            idAttr = idValue;
                        }

                        string? cid = null;
                        if (item.TryGetValue(userCidAttribute, out var cidAttr) &&
                            !string.IsNullOrWhiteSpace(cidAttr.S))
                        {
                            cid = cidAttr.S.Trim();
                        }

                        if (onlyVerified)
                        {
                            bool isVerified = item.TryGetValue(verifiedAttribute, out var verifiedAttr) &&
                                              verifiedAttr.BOOL;
                            if (!isVerified)
                                continue;
                        }

                        if (onlySubscribed)
                        {
                            bool unsubscribed = item.TryGetValue(unsubscribedAttribute, out var unsubAttr) &&
                                                unsubAttr.BOOL;
                            if (unsubscribed)
                                continue;
                        }
                        recipients.Add(new UserRecipient(email, firstName, idAttr, cid));
                    }

                    lastEvaluatedKey = response.LastEvaluatedKey;
                    //if (iSent++ > nSendingLimit) { break; }

                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Fetched {recipients.Count} user emails.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to fetch user emails: {ex.Message}\r\n";
                }));
            }

            return recipients;
        }

        private async Task<List<UserRecipient>> FetchItemUserEmailsAsync(bool onlyVerified, bool onlySubscribed)
        {
            string tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";
            string emailAttribute = ConfigurationManager.AppSettings["UserEmailAttribute"] ?? "Email";
            string firstNameAttribute = ConfigurationManager.AppSettings["UserFirstNameAttribute"] ?? "FirstName";
            string userIdAttribute = ConfigurationManager.AppSettings["UserIdAttribute"] ?? "ID";
            string userCidAttribute = ConfigurationManager.AppSettings["UserCidAttribute"] ?? "cid";
            string verifiedAttribute = ConfigurationManager.AppSettings["UserVerifiedAttribute"] ?? "Verified";
            string unsubscribedAttribute = ConfigurationManager.AppSettings["UserUnsubscribedAttribute"] ?? "Unsubscribed";

            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recipients = new List<UserRecipient>();

            try
            {
                var scanRequest = new ScanRequest
                {
                    TableName = tableName,
                    FilterExpression = "begins_with(ID, :userPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":userPrefix", new AttributeValue { S = "USR#" } }
                    },
                    ProjectionExpression = $"{emailAttribute}, {firstNameAttribute}, {userIdAttribute}, {userCidAttribute}"
                };

                Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

                do
                {
                    scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                    var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        if (!item.TryGetValue(emailAttribute, out var emailAttr))
                            continue;

                        string? email = null;
                        if (!string.IsNullOrWhiteSpace(emailAttr.S))
                        {
                            email = emailAttr.S.Trim();
                        }
                        else if (emailAttr.L != null && emailAttr.L.Count > 0)
                        {
                            foreach (var entry in emailAttr.L)
                            {
                                if (!string.IsNullOrWhiteSpace(entry.S))
                                {
                                    email = entry.S.Trim();
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(email))
                            continue;

                        if (!emails.Add(email))
                            continue;

                        string? firstName = null;
                        if (item.TryGetValue(firstNameAttribute, out var firstNameAttr))
                        {
                            if (!string.IsNullOrWhiteSpace(firstNameAttr.S))
                            {
                                firstName = firstNameAttr.S.Trim();
                            }
                            else if (firstNameAttr.L != null && firstNameAttr.L.Count > 0)
                            {
                                firstName = firstNameAttr.L
                                    .Select(entry => entry.S)
                                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                                    ?.Trim();
                            }
                        }

                        AttributeValue? idAttr = null;
                        if (item.TryGetValue(userIdAttribute, out var idValue))
                        {
                            idAttr = idValue;
                        }

                        string? cid = null;
                        if (item.TryGetValue(userCidAttribute, out var cidAttr) &&
                            !string.IsNullOrWhiteSpace(cidAttr.S))
                        {
                            cid = cidAttr.S.Trim();
                        }

                        if (onlyVerified)
                        {
                            bool isVerified = item.TryGetValue(verifiedAttribute, out var verifiedAttr) &&
                                              verifiedAttr.BOOL;
                            if (!isVerified)
                                continue;
                        }

                        if (onlySubscribed)
                        {
                            bool unsubscribed = item.TryGetValue(unsubscribedAttribute, out var unsubAttr) &&
                                                unsubAttr.BOOL;
                            if (unsubscribed)
                                continue;
                        }

                        recipients.Add(new UserRecipient(email, firstName, idAttr, cid));
                    }

                    lastEvaluatedKey = response.LastEvaluatedKey;
                } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"[CrossStitchItems] Fetched {recipients.Count} user emails.\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"[CrossStitchItems] Failed to fetch user emails: {ex.Message}\r\n";
                }));
            }

            return recipients;
        }

        private async Task SendAdminUserStyleEmailAsync(IReadOnlyList<AlbumInfo> albumSuggestions)
        {
            string? sender = ConfigurationManager.AppSettings["SenderEmail"];
            string? admin = ConfigurationManager.AppSettings["AdminEmail"];
            string facebookUrl = "https://www.facebook.com/AnnCrossStitch/";

            if (PatternInfo == null || string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(admin))
                return;

            int designId = PatternInfo.DesignID;
            string subject = UserEmailSubject;
            string patternUrl = _linkHelper.BuildPatternUrl(PatternInfo);
            string imageUrl = _linkHelper.BuildImageUrl(designId, _albumId);
            string siteUrl = patternUrl;
            if (Uri.TryCreate(patternUrl, UriKind.Absolute, out var patternUri))
                siteUrl = patternUri.GetLeftPart(UriPartial.Authority);
            string altText = string.IsNullOrWhiteSpace(PatternInfo.Title)
                ? "New cross stitch pattern"
                : PatternInfo.Title;
            string eid = DateTime.UtcNow.ToString("yyMMdd", CultureInfo.InvariantCulture);
            string cid = "admin";

            string patternUrlWithTracking = AppendTrackingParameters(patternUrl, cid, eid);
            string siteUrlWithTracking = AppendTrackingParameters(siteUrl, cid, eid);
            string imageUrlForEmail = imageUrl;
            string userAlbumHtml = BuildAlbumSuggestionsHtml(albumSuggestions, cid, eid);
            string userAlbumText = BuildAlbumSuggestionsText(albumSuggestions, cid, eid);

            string unsubscribeUrl = BuildUnsubscribeUrl(admin);
            var unsubscribeHeaders = BuildUnsubscribeHeaders(unsubscribeUrl, sender);
            string greetingText = BuildUserGreetingText("admin");
            string greetingHtml = BuildUserGreetingHtml("admin");
            string userBaseTextBody = BuildUserBaseTextBody(patternUrlWithTracking, siteUrlWithTracking, facebookUrl);
            string userBaseHtmlBody = BuildUserBaseHtmlBody(patternUrlWithTracking, imageUrlForEmail, siteUrlWithTracking, facebookUrl, altText);
            string userText = greetingText + userBaseTextBody + userAlbumText + $"\r\nUnsubscribe: {unsubscribeUrl}";
            string userHtml = greetingHtml + userBaseHtmlBody + userAlbumHtml + $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{unsubscribeUrl}\">unsubscribe</a>.</p>";

            await _emailHelper.SendEmailAsync(
                _sesClient,
                sender,
                new[] { admin },
                subject,
                userText,
                userHtml,
                unsubscribeHeaders).ConfigureAwait(false);
        }

        private async Task SendNotificationMailToUsersAsync(
            int designId,
            string pinId,
            List<UserRecipient> userRecipients,
            IReadOnlyList<AlbumInfo> albumSuggestions)
        {
            string? sender = ConfigurationManager.AppSettings["SenderEmail"];
            string? admin = ConfigurationManager.AppSettings["AdminEmail"];
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";
            string emailAttribute = ConfigurationManager.AppSettings["UserEmailAttribute"] ?? "Email";
            string userIdAttribute = ConfigurationManager.AppSettings["UserIdAttribute"] ?? "ID";
            string verifiedAttribute = ConfigurationManager.AppSettings["UserVerifiedAttribute"] ?? "Verified";
            string unsubscribedAttribute = ConfigurationManager.AppSettings["UserUnsubscribedAttribute"] ?? "Unsubscribed";
            string facebookUrl = "https://www.facebook.com/AnnCrossStitch/";

            if (PatternInfo == null || string.IsNullOrEmpty(sender) || userRecipients.Count == 0)
                return;

            string subject = UserEmailSubject;
            string patternUrl = _linkHelper.BuildPatternUrl(PatternInfo);
            string imageUrl = _linkHelper.BuildImageUrl(designId, _albumId);
            string siteUrl = patternUrl;
            if (Uri.TryCreate(patternUrl, UriKind.Absolute, out var patternUri))
                siteUrl = patternUri.GetLeftPart(UriPartial.Authority);
            string altText = string.IsNullOrWhiteSpace(PatternInfo.Title)
                ? "New cross stitch pattern"
                : PatternInfo.Title;
            string eid = DateTime.UtcNow.ToString("yyMMdd", CultureInfo.InvariantCulture);

            string patternUrlWithUtm = AppendUtmParameters(patternUrl);
            string siteUrlWithUtm = AppendUtmParameters(siteUrl);
            string baseTextBody = BuildUserBaseTextBody(patternUrlWithUtm, siteUrlWithUtm, facebookUrl);
            string baseHtmlBody = BuildUserBaseHtmlBody(patternUrlWithUtm, imageUrl, siteUrlWithUtm, facebookUrl, altText);
            string albumHtml = BuildAlbumSuggestionsHtml(albumSuggestions);
            string albumText = BuildAlbumSuggestionsText(albumSuggestions);

            // Send the same email to admin first.
            if (!string.IsNullOrEmpty(admin))
            {
                string adminUnsubUrl = BuildUnsubscribeUrl(admin);
                var adminHeaders = BuildUnsubscribeHeaders(adminUnsubUrl, sender);
                string adminTextBody = baseTextBody + albumText + $"\r\nUnsubscribe: {adminUnsubUrl}";
                string adminHtmlBody = baseHtmlBody + $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{adminUnsubUrl}\">unsubscribe</a>.</p>";

                await _emailHelper.SendEmailAsync(
                    _sesClient,
                    sender,
                    new[] { admin },
                    subject,
                    adminTextBody,
                    adminHtmlBody,
                    adminHeaders).ConfigureAwait(false);
            }

            var recipients = userRecipients;
            if (!string.IsNullOrEmpty(admin))
            {
                recipients = userRecipients
                    .Where(r => !string.Equals(r.Email, admin, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            int totalUserCount = await CountUsersAsync(
                    usersTable,
                    $"{verifiedAttribute} = :trueVal AND (attribute_not_exists({unsubscribedAttribute}) OR {unsubscribedAttribute} = :falseVal)",
                    new Dictionary<string, AttributeValue>
                    {
                        { ":trueVal", new AttributeValue { BOOL = true } },
                        { ":falseVal", new AttributeValue { BOOL = false } }
                    })
                .ConfigureAwait(false);
            await SendEmailsWithProgressAsync(
                "[CrossStitchUsers]",
                recipients,
                subject,
                sender,
                patternUrl,
                siteUrl,
                imageUrl,
                facebookUrl,
                altText,
                albumSuggestions,
                eid,
                totalUserCount,
                true,
                usersTable,
                emailAttribute,
                userIdAttribute).ConfigureAwait(false);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += $"Sent notification email to {recipients.Count} verified, subscribed users from CrossStitchUsers.\r\n";
            }));
        }

        private async Task UpdateLastEmailDateAsync(
            UserRecipient recipient,
            string usersTable,
            string emailAttribute,
            string userIdAttribute)
        {
            var key = new Dictionary<string, AttributeValue>();

            if (recipient.IdAttribute != null)
            {
                key[userIdAttribute] = recipient.IdAttribute;
            }
            else
            {
                key[emailAttribute] = new AttributeValue { S = recipient.Email };
            }

            var updateRequest = new UpdateItemRequest
            {
                TableName = usersTable,
                Key = key,
                UpdateExpression = "SET LastEmailDate = :now",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":now"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
                }
            };

            await _dynamoDbClient.UpdateItemAsync(updateRequest).ConfigureAwait(false);
        }

        private string GetPhotoKey(int designId)
        {
            string fileName = Path.GetFileName(_imageFilePath);
            return $"{PhotoPrefix}/{_albumId}/{designId}/{fileName}";
        }

        private string BuildUnsubscribeUrl(string email)
        {
            string configuredBaseUrl = ConfigurationManager.AppSettings["UnsubscribeBaseUrl"];
            string baseUrl = !string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? configuredBaseUrl.TrimEnd('/')
                : $"{_linkHelper.SiteBaseUrl}/unsubscribe";
            string secret = ConfigurationManager.AppSettings["UnsubscribeSecret"] ??
                            "change-me-secret-for-unsubscribe-hmac";

            string token = BuildUnsubscribeToken(email, secret);

            return $"{baseUrl}?token={Uri.EscapeDataString(token)}";
        }

        private static string BuildUnsubscribeToken(string email, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(email));
            return ToBase64Url(hash);
        }

        /// <summary>
        /// Generates a cryptographically secure random token
        /// encoded as URL-safe base64 (same style as ToBase64Url).
        /// </summary>
        private static string GenerateRandomToken(int size = 32)
        {
            var data = new byte[size];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(data);
            }

            return ToBase64Url(data);
        }

        private static string ToBase64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string AppendTrackingParameters(string url, string? cid, string? eid)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(cid))
                queryParts.Add($"cid={Uri.EscapeDataString(cid)}");
            if (!string.IsNullOrWhiteSpace(eid))
                queryParts.Add($"eid={Uri.EscapeDataString(eid)}");

            string trackedUrl = queryParts.Count == 0
                ? url
                : AppendQueryParameters(url, queryParts);

            return AppendUtmParameters(trackedUrl);
        }

        private static string AppendUtmParameters(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            var queryParts = new List<string>();

            if (!HasQueryParameter(url, "utm_source"))
                queryParts.Add("utm_source=newsletter");
            if (!HasQueryParameter(url, "utm_medium"))
                queryParts.Add("utm_medium=email");
            if (!HasQueryParameter(url, "utm_campaign"))
            {
                string campaign = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                queryParts.Add($"utm_campaign={Uri.EscapeDataString(campaign)}");
            }

            return queryParts.Count == 0
                ? url
                : AppendQueryParameters(url, queryParts);
        }

        private static bool HasQueryParameter(string url, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(parameterName))
                return false;

            int queryIndex = url.IndexOf('?');
            if (queryIndex < 0)
                return false;

            string query = url.Substring(queryIndex + 1);
            int hashIndex = query.IndexOf('#');
            if (hashIndex >= 0)
                query = query.Substring(0, hashIndex);

            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eqIndex = part.IndexOf('=');
                string name = eqIndex >= 0 ? part.Substring(0, eqIndex) : part;
                if (string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string AppendQueryParameters(string url, IReadOnlyList<string> parameters)
        {
            if (string.IsNullOrWhiteSpace(url) || parameters == null || parameters.Count == 0)
                return url;

            string fragment = string.Empty;
            string baseUrl = url;
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                fragment = url.Substring(hashIndex);
                baseUrl = url.Substring(0, hashIndex);
            }

            string separator = baseUrl.Contains("?") ? "&" : "?";
            return $"{baseUrl}{separator}{string.Join("&", parameters)}{fragment}";
        }

        private static Dictionary<string, string> BuildUnsubscribeHeaders(string unsubscribeUrl, string sender)
        {
            string mailto = $"mailto:{sender}";
            return new Dictionary<string, string>
            {
                { "List-Unsubscribe", $"<{mailto}>, <{unsubscribeUrl}>" },
                { "List-Unsubscribe-Post", "List-Unsubscribe=One-Click" }
            };
        }

        private static string ConvertPlainTextToHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var paragraphs = normalized.Split(new[] { "\n\n" }, StringSplitOptions.None);
            var htmlParagraphs = paragraphs
                .Select(p => WebUtility.HtmlEncode(p).Replace("\n", "<br/>"))
                .Where(p => !string.IsNullOrWhiteSpace(p));

            return string.Join(string.Empty, htmlParagraphs.Select(p => $"<p>{p}</p>"));
        }

        private static string PersonalizeTextTemplate(string template, string? firstName)
        {
            if (string.IsNullOrWhiteSpace(template))
                return string.Empty;

            string name = string.IsNullOrWhiteSpace(firstName) ? "friend" : firstName.Trim();
            return template.Replace("<username>", name);
        }

        private static string PersonalizeHtmlTemplate(string template, string? firstName)
        {
            if (string.IsNullOrWhiteSpace(template))
                return string.Empty;

            string name = string.IsNullOrWhiteSpace(firstName) ? "friend" : firstName.Trim();
            string encodedName = WebUtility.HtmlEncode(name);
            return template
                .Replace("&lt;username&gt;", encodedName)
                .Replace("<username>", encodedName);
        }

        private async Task SendTextEmailsWithProgressAsync(
            string label,
            List<UserRecipient> recipients,
            string subject,
            string sender,
            string baseTextBody,
            string baseHtmlBody,
            int totalCount,
            string usersTable,
            string emailAttribute,
            string userIdAttribute)
        {
            if (recipients == null || recipients.Count == 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"{label} No recipients found.\r\n";
                }));
                return;
            }

            int targetCount = Math.Max(totalCount, recipients.Count);
            var stopwatch = Stopwatch.StartNew();
            int sent = 0;

            foreach (var recipient in recipients)
            {
                string unsubscribeUrl = BuildUnsubscribeUrl(recipient.Email);
                var unsubscribeHeaders = BuildUnsubscribeHeaders(unsubscribeUrl, sender);
                string personalizedText = PersonalizeTextTemplate(baseTextBody, recipient.FirstName);
                string personalizedHtml = PersonalizeHtmlTemplate(baseHtmlBody, recipient.FirstName);

                string textBody = personalizedText + $"\r\n\r\nUnsubscribe: {unsubscribeUrl}";
                string? htmlBody = string.IsNullOrWhiteSpace(personalizedHtml)
                    ? null
                    : personalizedHtml + $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{unsubscribeUrl}\">unsubscribe</a>.</p>";

                await _emailHelper.SendEmailAsync(
                    _sesClient,
                    sender,
                    new[] { recipient.Email },
                    subject,
                    textBody,
                    htmlBody,
                    unsubscribeHeaders).ConfigureAwait(false);

                try
                {
                    await UpdateLastEmailDateAsync(recipient, usersTable, emailAttribute, userIdAttribute)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text += $"{label} Failed to update LastEmailDate for {recipient.Email}: {ex.Message}\r\n";
                    }));
                }

                sent++;

                if (sent % 50 == 0 || sent == recipients.Count)
                {
                    TimeSpan elapsed = stopwatch.Elapsed;
                    double avgSeconds = sent > 0 ? elapsed.TotalSeconds / sent : 0;
                    int remaining = Math.Max(targetCount - sent, 0);
                    TimeSpan eta = avgSeconds > 0 ? TimeSpan.FromSeconds(avgSeconds * remaining) : TimeSpan.Zero;
                    double percentRemaining = targetCount > 0 ? (remaining * 100.0 / targetCount) : 0;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text +=
                            $"{label} Sent {sent}/{targetCount} | Elapsed {elapsed:hh\\:mm\\:ss} | Avg {avgSeconds:F2}s/email | ETA {eta:hh\\:mm\\:ss} | Remaining {remaining} ({percentRemaining:F1}% left).\r\n";
                    }));
                }
            }

            stopwatch.Stop();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += $"{label} Finished sending {sent} email(s) in {stopwatch.Elapsed:hh\\:mm\\:ss}.\r\n";
            }));
        }

        private async Task SendEmailsWithProgressAsync(
            string label,
            List<UserRecipient> recipients,
            string subject,
            string sender,
            string patternUrl,
            string siteUrl,
            string imageUrl,
            string facebookUrl,
            string altText,
            IReadOnlyList<AlbumInfo> albumSuggestions,
            string eid,
            int totalCount,
            bool updateLastEmailDate,
            string usersTable,
            string emailAttribute,
            string userIdAttribute)
        {
            if (recipients == null || recipients.Count == 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"{label} No recipients found.\r\n";
                }));
                return;
            }

            int targetCount = Math.Max(totalCount, recipients.Count);
            var stopwatch = Stopwatch.StartNew();
            int sent = 0;

            foreach (var recipient in recipients)
            {
                string cid = recipient.Cid ?? string.Empty;
                string patternUrlWithTracking = AppendTrackingParameters(patternUrl, cid, eid);
                string siteUrlWithTracking = AppendTrackingParameters(siteUrl, cid, eid);
                string userAlbumHtml = BuildAlbumSuggestionsHtml(albumSuggestions, cid, eid);
                string userAlbumText = BuildAlbumSuggestionsText(albumSuggestions, cid, eid);

                string unsubscribeUrl = BuildUnsubscribeUrl(recipient.Email);
                var unsubscribeHeaders = BuildUnsubscribeHeaders(unsubscribeUrl, sender);
                string greetingText = BuildUserGreetingText(recipient.FirstName);
                string greetingHtml = BuildUserGreetingHtml(recipient.FirstName);
                string userBaseTextBody = BuildUserBaseTextBody(patternUrlWithTracking, siteUrlWithTracking, facebookUrl);
                string userBaseHtmlBody = BuildUserBaseHtmlBody(patternUrlWithTracking, imageUrl, siteUrlWithTracking, facebookUrl, altText);
                string userText = greetingText + userBaseTextBody + userAlbumText + $"\r\nUnsubscribe: {unsubscribeUrl}";
                string userHtml = greetingHtml + userBaseHtmlBody + userAlbumHtml + $"<p style=\"font-size:12px; color:#666;\">If you prefer not to receive these emails, <a href=\"{unsubscribeUrl}\">unsubscribe</a>.</p>";

                await _emailHelper.SendEmailAsync(
                    _sesClient,
                    sender,
                    new[] { recipient.Email },
                    subject,
                    userText,
                    userHtml,
                    unsubscribeHeaders).ConfigureAwait(false);

                if (updateLastEmailDate)
                {
                    try
                    {
                        await UpdateLastEmailDateAsync(recipient, usersTable, emailAttribute, userIdAttribute)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            txtStatus.Text += $"{label} Failed to update LastEmailDate for {recipient.Email}: {ex.Message}\r\n";
                        }));
                    }
                }

                sent++;

                if (sent % 50 == 0 || sent == recipients.Count)
                {
                    TimeSpan elapsed = stopwatch.Elapsed;
                    double avgSeconds = sent > 0 ? elapsed.TotalSeconds / sent : 0;
                    int remaining = Math.Max(targetCount - sent, 0);
                    TimeSpan eta = avgSeconds > 0 ? TimeSpan.FromSeconds(avgSeconds * remaining) : TimeSpan.Zero;
                    double percentRemaining = targetCount > 0 ? (remaining * 100.0 / targetCount) : 0;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text +=
                            $"{label} Sent {sent}/{targetCount} | Elapsed {elapsed:hh\\:mm\\:ss} | Avg {avgSeconds:F2}s/email | ETA {eta:hh\\:mm\\:ss} | Remaining {remaining} ({percentRemaining:F1}% left).\r\n";
                    }));
                }
            }

            stopwatch.Stop();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += $"{label} Finished sending {sent} email(s) in {stopwatch.Elapsed:hh\\:mm\\:ss}.\r\n";
            }));
        }

        private static string BuildUserGreetingText(string? firstName) =>
            !string.IsNullOrWhiteSpace(firstName)
                ? $"Hi {firstName},\r\n\r\n"
                : "Hi,\r\n\r\n";

        private static string BuildUserGreetingHtml(string? firstName) =>
            !string.IsNullOrWhiteSpace(firstName)
                ? $"<p>Hi {WebUtility.HtmlEncode(firstName)},</p>"
                : "<p>Hi,</p>";

        private static string BuildUserBaseTextBody(string viewAndDownloadUrl, string siteRootUrl, string facebookLink) =>
            "I just wanted to send a quick note! The PDF cross-stitch pattern for the Blue Bolt Buddy is finished and has been uploaded to the site. I think you'll really enjoy stitching this fun, little alien. I always love seeing what everyone creates.\r\n\r\n" +
            "You can download the pattern right here:\r\n" +
            $"{viewAndDownloadUrl}\r\n\r\n" +
            "Happy Stitching,\r\n" +
            $"Visit {siteRootUrl} to explore more patterns and see what I'm uploading next.\r\n" +
            $"Join me on Facebook: {facebookLink} - I'd love to connect.";

        private static string BuildUserBaseHtmlBody(string viewAndDownloadUrl, string imageSrcUrl, string siteRootUrl, string facebookLink, string alt) =>
            "<p>I just wanted to send a quick note! The PDF cross-stitch pattern for the Blue Bolt Buddy is finished and has been uploaded to the site. I think you'll really enjoy stitching this fun, little alien. I always love seeing what everyone creates.</p>" +
            "<p>You can download the pattern right here:</p>" +
            $"<p><a href=\"{viewAndDownloadUrl}\"><img src=\"{imageSrcUrl}\" alt=\"{WebUtility.HtmlEncode(alt)}\" style=\"max-width:280px; max-height:280px; width:auto; height:auto; border:0;\"></a></p>" +
            $"<p><a href=\"{viewAndDownloadUrl}\">Download the pattern here</a></p>" +
            "<p>Happy Stitching,</p>" +
            $"<p>Visit <a href=\"{siteRootUrl}\">{siteRootUrl}</a> to explore more patterns and see what I'm uploading next.</p>" +
            $"<p>Join me on Facebook: <a href=\"{facebookLink}\">Ann Cross Stitch</a>. I'd love to connect.</p>";

        private static List<string> ReadSuppressedEmails(string filePath)
        {
            var emails = new List<string>();

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Suppressed list file not found.", filePath);
            }

            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                // Every 3rd line starting at index 0 (0,3,6,...). If the source format differs,
                // adjust the stride logic here.
                if (i % 3 == 0 && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    emails.Add(lines[i].Trim());
                }
            }

            return emails;
        }

        private async Task RemoveSuppressedUsersAsync(List<string> emails)
        {
            string tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";
            int deletedCount = 0;
            int missingCount = 0;
            int missingNPageCount = 0;
            int errors = 0;
            var stopwatch = Stopwatch.StartNew();
            List<string> lstMissing = new List<string>();
            for (int index = 0; index < emails.Count; index++)
            {
                string email = emails[index];
                string userId = $"USR#{email}";

                try
                {
                    var queryRequest = new QueryRequest
                    {
                        TableName = tableName,
                        KeyConditionExpression = "ID = :id",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            { ":id", new AttributeValue { S = userId } }
                        },
                        ProjectionExpression = "ID, NPage"
                    };

                    var queryResponse = await _dynamoDbClient.QueryAsync(queryRequest).ConfigureAwait(false);
                    if (queryResponse.Items.Count == 0)
                    {
                        missingCount++;
                        lstMissing.Add(userId);
                        continue;
                    }

                    foreach (var item in queryResponse.Items)
                    {
                        if (!item.TryGetValue("NPage", out var nPageAttr) || string.IsNullOrWhiteSpace(nPageAttr.S))
                        {
                            missingNPageCount++;
                            continue;
                        }

                        var deleteRequest = new DeleteItemRequest
                        {
                            TableName = tableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { "ID", new AttributeValue { S = userId } },
                                { "NPage", nPageAttr }
                            }
                        };

                        await _dynamoDbClient.DeleteItemAsync(deleteRequest).ConfigureAwait(false);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text += $"[Suppress] Error for {email}: {ex.Message}\r\n";
                    }));
                }

                if ((index + 1) % 50 == 0 || index == emails.Count - 1)
                {
                    double avgSeconds = (index + 1) > 0 ? stopwatch.Elapsed.TotalSeconds / (index + 1) : 0;
                    int remaining = Math.Max(emails.Count - (index + 1), 0);
                    TimeSpan eta = avgSeconds > 0 ? TimeSpan.FromSeconds(avgSeconds * remaining) : TimeSpan.Zero;
                    double percentRemaining = emails.Count > 0 ? (remaining * 100.0 / emails.Count) : 0;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text +=
                            $"[Suppress] Processed {index + 1}/{emails.Count} | Deleted {deletedCount}, Missing {missingCount}, Missing NPage {missingNPageCount}, Errors {errors}. Elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}, ETA {eta:hh\\:mm\\:ss}, Remaining {remaining} ({percentRemaining:F1}% left).\r\n";
                    }));
                }
            }

            stopwatch.Stop();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += $"[Suppress] Done. Deleted {deletedCount}, Missing {missingCount}, Missing NPage {missingNPageCount}, Errors {errors}. Total time {stopwatch.Elapsed:hh\\:mm\\:ss}.\r\n";
            }));
        }

        private async Task MarkUsersVerifiedAsync()
        {
            string usersTable = ConfigurationManager.AppSettings["UsersTableName"] ?? "CrossStitchUsers";
            const string verifiedField = "Verified";
            const string verifiedAtField = "VerifiedAt";
            const string createdAtField = "CreatedAt";

            int updatedCount = 0;
            int skippedCount = 0;
            int missingCreatedAtCount = 0;
            int errors = 0;
            int scannedCount = 0;
            int totalCount = await CountUsersAsync(usersTable).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text += $"{usersTable}: total users {totalCount}.\r\n";
            }));

            var scanRequest = new ScanRequest
            {
                TableName = usersTable,
                ProjectionExpression = $"ID, {createdAtField}, {verifiedField}, {verifiedAtField}, NPage"
            };

            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            do
            {
                scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                var response = await _dynamoDbClient.ScanAsync(scanRequest).ConfigureAwait(false);

                foreach (var item in response.Items)
                {
                    scannedCount++;

                    if (!item.TryGetValue("ID", out var idAttr))
                        continue;

                    bool alreadyVerified = item.TryGetValue(verifiedField, out var verifiedAttr) && verifiedAttr.BOOL;
                    bool hasVerifiedAt = item.TryGetValue(verifiedAtField, out var verifiedAtAttr) && !string.IsNullOrWhiteSpace(verifiedAtAttr.S);

                    if (alreadyVerified && hasVerifiedAt)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!item.TryGetValue(createdAtField, out var createdAtAttr) || string.IsNullOrWhiteSpace(createdAtAttr.S))
                    {
                        missingCreatedAtCount++;
                        continue;
                    }

                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = usersTable,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            { "ID", idAttr }
                        },
                        UpdateExpression = $"SET {verifiedField} = :trueVal, {verifiedAtField} = :createdAt",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            { ":trueVal", new AttributeValue { BOOL = true } },
                            { ":createdAt", new AttributeValue { S = createdAtAttr.S } }
                        }
                    };

                    try
                    {
                        await _dynamoDbClient.UpdateItemAsync(updateRequest).ConfigureAwait(false);
                        updatedCount++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            txtStatus.Text += $"[Verify] Failed to update {idAttr.S}: {ex.Message}\r\n";
                        }));
                    }

                    if ((updatedCount + skippedCount + missingCreatedAtCount) % 50 == 0)
                    {
                        int remaining = totalCount > 0
                            ? Math.Max(totalCount - (updatedCount + skippedCount + missingCreatedAtCount), 0)
                            : -1;
                        double avgSeconds = scannedCount > 0 ? stopwatch.Elapsed.TotalSeconds / scannedCount : 0;
                        TimeSpan eta = avgSeconds > 0 && remaining >= 0
                            ? TimeSpan.FromSeconds(avgSeconds * remaining)
                            : TimeSpan.Zero;
                        double percentRemaining = totalCount > 0
                            ? (remaining * 100.0 / totalCount)
                            : 0;

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            string remainingText = remaining >= 0
                                ? $"{remaining} remaining ({percentRemaining:F1}% left)"
                                : "remaining: unknown";
                            txtStatus.Text +=
                                $"[Verify] Scanned {scannedCount}, updated {updatedCount}, skipped {skippedCount}, missing CreatedAt {missingCreatedAtCount}, errors {errors}. Elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}, ETA {eta:hh\\:mm\\:ss}, {remainingText}.\r\n";
                        }));
                    }
                }

                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            stopwatch.Stop();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text +=
                    $"[Verify] Done. Updated {updatedCount}, skipped {skippedCount}, missing CreatedAt {missingCreatedAtCount}, errors {errors}. Total time {stopwatch.Elapsed:hh\\:mm\\:ss}.\r\n";
            }));
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

        #region Missing PDFs audit

        private async Task<List<DesignLocation>> LoadAllDesignLocationsAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            var designs = new List<DesignLocation>();
            string tableName = ConfigurationManager.AppSettings["DynamoTableName"] ?? "CrossStitchItems";

            var scanRequest = new ScanRequest
            {
                TableName = tableName,
                FilterExpression = "EntityType = :designType",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":designType", new AttributeValue { S = "DESIGN" } }
                },
                ProjectionExpression = "AlbumID, DesignID, EntityType"
            };

            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            do
            {
                scanRequest.ExclusiveStartKey = lastEvaluatedKey;
                var response = await _dynamoDbClient.ScanAsync(scanRequest, cancellationToken).ConfigureAwait(false);
                lastEvaluatedKey = response.LastEvaluatedKey;

                foreach (var item in response.Items)
                {
                    if (!item.TryGetValue("AlbumID", out var albumAttr) ||
                        !item.TryGetValue("DesignID", out var designAttr))
                    {
                        continue;
                    }

                    if (!int.TryParse(albumAttr.N ?? albumAttr.S, out int albumId) ||
                        !int.TryParse(designAttr.N ?? designAttr.S, out int designId))
                    {
                        continue;
                    }

                    designs.Add(new DesignLocation(albumId, designId));
                }

                if (designs.Count > 0 && designs.Count % 200 == 0)
                {
                    progress?.Report($"Loaded {designs.Count} designs so far...");
                }
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0);

            progress?.Report($"Loaded {designs.Count} designs in total.");
            return designs;
        }

        private async Task<HashSet<string>> LoadAllPdfKeysAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var listRequest = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = "pdfs/"
            };

            int count = 0;
            var paginator = _s3Client.Paginators.ListObjectsV2(listRequest);

            await foreach (var obj in paginator.S3Objects.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                keys.Add(obj.Key);
                count++;

                if (count % 500 == 0)
                {
                    progress?.Report($"Indexed {count} PDF objects so far...");
                }
            }

            progress?.Report($"Indexed {count} PDF objects.");
            return keys;
        }

        private static List<MissingPdfInfo> FindDesignsWithMissingPdfs(
            IEnumerable<DesignLocation> designs,
            IReadOnlySet<string> existingPdfKeys)
        {
            var missing = new List<MissingPdfInfo>();

            foreach (var design in designs)
            {
                var expectedKeys = BuildExpectedPdfKeys(design);
                var missingKeys = expectedKeys
                    .Where(key => !existingPdfKeys.Contains(key))
                    .ToList();

                if (missingKeys.Count > 0)
                {
                    if (design.DesignId == 5366) 
                    { 
                    }
                    missing.Add(new MissingPdfInfo(design.DesignId, design.AlbumId, missingKeys));
                }
            }

            return missing;
        }

        private static List<string> BuildExpectedPdfKeys(DesignLocation design)
        {
            var keys = new List<string>(RequiredPdfVariants.Length + 1);
            string albumPart = design.AlbumId.ToString();
            string designPart = design.DesignId.ToString();

            foreach (string variant in RequiredPdfVariants)
            {
                keys.Add($"pdfs/{albumPart}/{designPart}/Stitch{designPart}_{variant}_Kit.pdf");
            }

            keys.Add($"pdfs/{albumPart}/Stitch{designPart}_Kit.pdf");
            return keys;
        }

        private static async Task WriteMissingPdfReportAsync(string reportPath, List<MissingPdfInfo> missingDesigns)
        {
            var lines = missingDesigns.Count == 0
                ? new[] { "All required PDFs are present." }
                : missingDesigns
                    .OrderBy(m => m.DesignId)
                    .Select(m => $"{m.DesignId},{m.AlbumId}");

            await File.WriteAllLinesAsync(reportPath, lines).ConfigureAwait(false);
        }

        private sealed record DesignLocation(int AlbumId, int DesignId);

        private sealed record MissingPdfInfo(int DesignId, int AlbumId, List<string> MissingKeys);

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

        private async void InitializeItemsUserCid_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text += "Initializing cid fields for CrossStitchItems users...\r\n";
            try
            {
                await InitializeItemsUserCidFieldsAsync();
                // Back on UI thread
                txtStatus.Text += "Finished initializing cid fields for CrossStitchItems users.\r\n";
            }
            catch (Exception ex)
            {
                txtStatus.Text += $"Error: {ex.Message}\r\n";
                MessageBox.Show(ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SendAdminUserEmail_Click(object sender, RoutedEventArgs e)
        {
            if (PatternInfo == null)
            {
                txtStatus.Text += "Extract PDF info before sending admin email.\r\n";
                return;
            }

            txtStatus.Text += "Sending user-style email to admin (1 email)...\r\n";
            try
            {
                var albumSuggestions = await FetchAlbumSuggestionsAsync(4).ConfigureAwait(false);
                await SendAdminUserStyleEmailAsync(albumSuggestions).ConfigureAwait(false);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += "Sent user-style email to admin (1/1).\r\n";
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to send admin user-style email: {ex.Message}\r\n";
                }));
            }
        }

        private async void RemoveSuppressedUsers_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text += "Starting removal of suppressed users...\r\n";

            try
            {
                var emails = ReadSuppressedEmails(SuppressedListPath);
                if (emails.Count == 0)
                {
                    txtStatus.Text += "No emails found to remove.\r\n";
                    return;
                }

                await RemoveSuppressedUsersAsync(emails).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to remove suppressed users: {ex.Message}\r\n";
                }));
            }
        }

        private async void MarkUsersVerified_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text += "Starting to mark users as verified...\r\n";

            try
            {
                await MarkUsersVerifiedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtStatus.Text += $"Failed to mark users verified: {ex.Message}\r\n";
                }));
            }
        }

        #endregion
    }
}

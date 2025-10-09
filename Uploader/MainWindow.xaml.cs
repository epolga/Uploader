using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms; // For FolderBrowserDialog

namespace Uploader
{
    public partial class MainWindow : Window
    {
        private string selectedFolder = string.Empty;
        private readonly string  bucketName = "cross-stitch-designs";
        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    selectedFolder = dialog.SelectedPath;
                    txtFolderPath.Text = selectedFolder;
                }
            }
        }

        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedFolder))
            {
                System.Windows.Forms.MessageBox.Show("Please select a folder first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                InitialDirectory = selectedFolder,
                Filter = "All files (*.*)|*.*", // Adjust filter as needed
                Title = "Select a file to upload"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtFilePath.Text = dialog.FileName;
            }
        }

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            //  string bucketName = txtBucketName.Text.Trim();


            string filePath = selectedFolder;

            txtStatus.Text = "Uploading...";
            try
            {
                // Use default credentials provider chain
                using (var client = new AmazonS3Client())
                {
                    var uploadRequest = new TransferUtilityUploadRequest
                    {
                        FilePath = filePath,
                        BucketName = bucketName,
                        Key = Path.GetFileName(filePath) // Use file name as S3 key; customize if needed
                    };

                    var transferUtility = new TransferUtility(client);
                    await transferUtility.UploadAsync(uploadRequest);
                }

                txtStatus.Text = "Upload completed successfully.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Upload failed: {ex.Message}";
                System.Windows.Forms.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
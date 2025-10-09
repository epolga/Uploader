using Amazon.S3;
using Amazon.S3.Model;
using System;
using System.Threading.Tasks;

namespace Uploader
{
    class UploaderHelper
    {
        private static readonly string bucketName = "cross-stitch-designs";
        private static readonly string s3PDFPrefix = "pdfs/";
        private static readonly string localFolderPath = @"D:\FTPROOT\Kits";
        private static readonly AmazonS3Client s3Client = new AmazonS3Client(Amazon.RegionEndpoint.USEast1);

        public static async Task UploadPdfAsync(string filePath, string s3Key)
        {
            try
            {
                Console.WriteLine($"Uploading {filePath} to s3://{bucketName}/{s3Key}");

                var putRequest = new PutObjectRequest
                {
                    BucketName = bucketName,
                    Key = s3Key,
                    FilePath = filePath,
                    ContentType = "application/pdf" // Assuming all files are PDFs
                };

                var response = await s3Client.PutObjectAsync(putRequest);
                Console.WriteLine($"Uploaded {s3Key} successfully. ETag: {response.ETag}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading {s3Key}: {ex.Message}");
            }
        }
    }
}

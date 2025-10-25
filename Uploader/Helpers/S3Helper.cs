using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using System;
using System.Threading.Tasks;

namespace Uploader.Helpers
{
    class S3Helper
    {
        private readonly string m_strBucketName;// = "cross-stitch-designs";
        //private readonly string s3PDFPrefix = "pdfs/";
        //private readonly string localFolderPath = @"D:\FTPROOT\Kits";
        private AmazonS3Client m_s3Client;

        public S3Helper(RegionEndpoint regionEndpoint, string bucketName)
        {
            m_s3Client = new AmazonS3Client(regionEndpoint);
            m_strBucketName = bucketName;
        }
        public async Task UploadPdfAsync(string filePath, string s3Key, string localFolderPath)
        {
            try
            {
                Console.WriteLine($"Uploading {filePath} to s3://{m_strBucketName}/{s3Key}");

                var putRequest = new PutObjectRequest
                {
                    BucketName = m_strBucketName,
                    Key = s3Key,
                    FilePath = filePath,
                    ContentType = "application/pdf" // Assuming all files are PDFs
                };

                var response = await m_s3Client.PutObjectAsync(putRequest);
                Console.WriteLine($"Uploaded {s3Key} successfully. ETag: {response.ETag}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading {s3Key}: {ex.Message}");
            }
        }

        public async Task DeleteFileAsync(string objectKey)
        {
            try
            {
                var deleteRequest = new DeleteObjectRequest
                {
                    BucketName = m_strBucketName,
                    Key = objectKey
                };

                var response = await m_s3Client.DeleteObjectAsync(deleteRequest);

                if (response.HttpStatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    Console.WriteLine($"Object '{objectKey}' deleted successfully from bucket '{m_strBucketName}'.");
                }
                else
                {
                    Console.WriteLine($"Failed to delete object '{objectKey}' from bucket '{m_strBucketName}'. Status: {response.HttpStatusCode}");
                }
            }
            catch (AmazonS3Exception ex)
            {
                Console.WriteLine($"Amazon S3 error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General error: {ex.Message}");
            }
        }
    }
}

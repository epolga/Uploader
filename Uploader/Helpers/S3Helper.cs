using System;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Uploader.Helpers
{
    /// <summary>
    /// Small helper to upload/delete files in S3.
    /// </summary>
    public class S3Helper
    {
        private readonly string _bucketName;
        private readonly AmazonS3Client _s3Client;

        public S3Helper(RegionEndpoint regionEndpoint, string bucketName)
        {
            _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));
            _s3Client = new AmazonS3Client(regionEndpoint);
        }

        /// <summary>
        /// Uploads a local PDF file to S3 at the given key.
        /// </summary>
        public async Task UploadPdfAsync(string filePath, string s3Key, string localFolderPath)
        {
            try
            {
                Console.WriteLine($"Uploading {filePath} to s3://{_bucketName}/{s3Key}");

                var putRequest = new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = s3Key,
                    FilePath = filePath,
                    ContentType = "application/pdf"
                };

                var response = await _s3Client.PutObjectAsync(putRequest).ConfigureAwait(false);
                Console.WriteLine($"Uploaded {s3Key} successfully. ETag: {response.ETag}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading {s3Key}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes an object from the S3 bucket.
        /// </summary>
        public async Task DeleteFileAsync(string objectKey)
        {
            try
            {
                var deleteRequest = new DeleteObjectRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey
                };

                var response = await _s3Client.DeleteObjectAsync(deleteRequest).ConfigureAwait(false);

                if (response.HttpStatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    Console.WriteLine($"Object '{objectKey}' deleted successfully from bucket '{_bucketName}'.");
                }
                else
                {
                    Console.WriteLine($"Failed to delete object '{objectKey}' from bucket '{_bucketName}'. Status: {response.HttpStatusCode}");
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

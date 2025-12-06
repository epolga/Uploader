using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;

namespace Uploader.Helpers
{
    /// <summary>
    /// Utility for sending emails through AWS SES.
    /// </summary>
    public class EmailHelper
    {
        public async Task SendEmailAsync(
            AmazonSimpleEmailServiceClient sesClient,
            string sender,
            IEnumerable<string> recipients,
            string subject,
            string textBody,
            string? htmlBody = null,
            IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default)
        {
            if (headers != null && headers.Count > 0)
            {
                await SendRawEmailAsync(sesClient, sender, recipients, subject, textBody, htmlBody, headers, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var destination = new Destination
            {
                ToAddresses = recipients.ToList()
            };

            var body = new Body
            {
                Text = new Content(textBody)
            };

            if (!string.IsNullOrWhiteSpace(htmlBody))
            {
                body.Html = new Content(htmlBody);
            }

            var request = new SendEmailRequest
            {
                Source = sender,
                Destination = destination,
                Message = new Amazon.SimpleEmail.Model.Message
                {
                    Subject = new Content(subject),
                    Body = body
                }
            };

            await sesClient.SendEmailAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static async Task SendRawEmailAsync(
            AmazonSimpleEmailServiceClient sesClient,
            string sender,
            IEnumerable<string> recipients,
            string subject,
            string textBody,
            string? htmlBody,
            IDictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            string htmlPart = htmlBody ?? System.Net.WebUtility.HtmlEncode(textBody);
            string boundary = "NextPart_" + System.Guid.NewGuid().ToString("N");
            string toHeader = string.Join(", ", recipients);

            var sb = new StringBuilder();
            sb.AppendLine($"From: {sender}");
            sb.AppendLine($"To: {toHeader}");
            sb.AppendLine($"Subject: {subject}");
            foreach (var header in headers)
            {
                sb.AppendLine($"{header.Key}: {header.Value}");
            }
            sb.AppendLine("MIME-Version: 1.0");
            sb.AppendLine($"Content-Type: multipart/alternative; boundary=\"{boundary}\"");
            sb.AppendLine();

            sb.AppendLine($"--{boundary}");
            sb.AppendLine("Content-Type: text/plain; charset=\"UTF-8\"");
            sb.AppendLine("Content-Transfer-Encoding: 7bit");
            sb.AppendLine();
            sb.AppendLine(textBody);
            sb.AppendLine();

            sb.AppendLine($"--{boundary}");
            sb.AppendLine("Content-Type: text/html; charset=\"UTF-8\"");
            sb.AppendLine("Content-Transfer-Encoding: 7bit");
            sb.AppendLine();
            sb.AppendLine(htmlPart);
            sb.AppendLine();
            sb.AppendLine($"--{boundary}--");

            var rawMessage = new RawMessage
            {
                Data = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()))
            };

            var request = new SendRawEmailRequest
            {
                Source = sender,
                Destinations = recipients.ToList(),
                RawMessage = rawMessage
            };

            try
            {
                await sesClient.SendRawEmailAsync(request, cancellationToken).ConfigureAwait(false);

            }
            catch (Exception ex)
            {
                string strMessage = ex.Message;
            }
        }
    }
}

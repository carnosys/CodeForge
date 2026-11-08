using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CodeForge.Infrastructure.Reports;

public sealed class BlobReportStore
{
    private readonly BlobContainerClient _container;

    public BlobReportStore(Uri serviceUri, string containerName)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var serviceClient = new BlobServiceClient(
            serviceUri,
            new DefaultAzureCredential());

        _container = serviceClient.GetBlobContainerClient(containerName);
    }

    public async Task SaveAsync(
        Guid jobId,
        ReadOnlyMemory<byte> pdf,
        CancellationToken cancellationToken = default)
    {
        if (pdf.IsEmpty)
        {
            throw new ArgumentException("The PDF report cannot be empty.", nameof(pdf));
        }

        try
        {
            await _container.CreateIfNotExistsAsync(
                PublicAccessType.None,
                cancellationToken: cancellationToken);

            var blob = _container.GetBlobClient($"jobs/{jobId:N}/report.pdf");
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/pdf",
                    ContentDisposition = $"attachment; filename=codeforge-report-{jobId:N}.pdf"
                },
                Metadata = new Dictionary<string, string>
                {
                    ["jobId"] = jobId.ToString()
                }
            };

            await blob.UploadAsync(
                BinaryData.FromBytes(pdf),
                options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ReportStorageException(
                $"The report for job {jobId} could not be saved to Blob Storage.",
                exception);
        }
    }
}

public sealed class ReportStorageException : Exception
{
    public ReportStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

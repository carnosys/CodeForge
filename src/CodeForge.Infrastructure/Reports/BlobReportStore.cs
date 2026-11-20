using Azure.Identity;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CodeForge.Infrastructure.Reports;

public interface IAnalysisArtifactStore
{
    Task SaveDependencyGraphAsync(
        Guid jobId,
        ReadOnlyMemory<byte> png,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default);

    Task SaveDotNetProjectGraphAsync(
        Guid jobId,
        ReadOnlyMemory<byte> png,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default);
}

public sealed class BlobReportStore : IAnalysisArtifactStore
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

    public async Task SaveDependencyGraphAsync(
        Guid jobId,
        ReadOnlyMemory<byte> png,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        if (png.IsEmpty || jpeg.IsEmpty)
        {
            throw new ArgumentException("Dependency graph artifacts cannot be empty.");
        }

        try
        {
            await _container.CreateIfNotExistsAsync(
                PublicAccessType.None,
                cancellationToken: cancellationToken);

            await UploadArtifactAsync(
                $"jobs/{jobId:N}/dependency-graph.png",
                png,
                "image/png",
                jobId,
                cancellationToken);
            await UploadArtifactAsync(
                $"jobs/{jobId:N}/dependency-graph.jpg",
                jpeg,
                "image/jpeg",
                jobId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ArtifactStorageException(
                $"The dependency graph for job {jobId} could not be saved to Blob Storage.",
                exception);
        }
    }

    public Task<ReadOnlyMemory<byte>?> GetDependencyGraphPngAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return DownloadArtifactAsync(
            $"jobs/{jobId:N}/dependency-graph.png",
            cancellationToken);
    }

    public Task<ReadOnlyMemory<byte>?> GetDependencyGraphJpegAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return DownloadArtifactAsync(
            $"jobs/{jobId:N}/dependency-graph.jpg",
            cancellationToken);
    }

    public async Task SaveDotNetProjectGraphAsync(
        Guid jobId,
        ReadOnlyMemory<byte> png,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        if (png.IsEmpty || jpeg.IsEmpty) throw new ArgumentException("Project graph artifacts cannot be empty.");
        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            await UploadArtifactAsync($"jobs/{jobId:N}/dotnet-project-graph.png", png, "image/png", jobId, cancellationToken);
            await UploadArtifactAsync($"jobs/{jobId:N}/dotnet-project-graph.jpg", jpeg, "image/jpeg", jobId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new ArtifactStorageException($"The .NET project graph for job {jobId} could not be saved.", exception);
        }
    }

    public Task<ReadOnlyMemory<byte>?> GetDotNetProjectGraphPngAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        DownloadArtifactAsync($"jobs/{jobId:N}/dotnet-project-graph.png", cancellationToken);

    public Task<ReadOnlyMemory<byte>?> GetDotNetProjectGraphJpegAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        DownloadArtifactAsync($"jobs/{jobId:N}/dotnet-project-graph.jpg", cancellationToken);

    private async Task UploadArtifactAsync(
        string path,
        ReadOnlyMemory<byte> content,
        string contentType,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(path);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string>
            {
                ["jobId"] = jobId.ToString()
            }
        };
        await blob.UploadAsync(
            BinaryData.FromBytes(content),
            options,
            cancellationToken);
    }

    private async Task<ReadOnlyMemory<byte>?> DownloadArtifactAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container
                .GetBlobClient(path)
                .DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToMemory();
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ArtifactStorageException(
                $"The Blob Storage artifact '{path}' could not be downloaded.",
                exception);
        }
    }
}

public class ArtifactStorageException : Exception
{
    public ArtifactStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ReportStorageException : ArtifactStorageException
{
    public ReportStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

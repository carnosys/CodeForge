using CodeForge.Api.Reports;
using CodeForge.Dtos;
using CodeForge.Helpers;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using CodeForge.Infrastructure.Models;
using CodeForge.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;


[ApiController]
[Route("api/[controller]")]
public class JobController : ControllerBase
{
    private readonly CodeForgeDbContext _context;
    private readonly ServiceBusProducer _producer;
    private readonly BlobReportStore _reportStore;
    private readonly ILogger<JobController> _logger;

    public JobController(
        CodeForgeDbContext context,
        ServiceBusProducer producer,
        BlobReportStore reportStore,
        ILogger<JobController> logger)
    {
        _context = context;
        _producer = producer;
        _reportStore = reportStore;
        _logger = logger;
    }

   [HttpGet]
    public async Task<IActionResult> GetJobs()
    {
     var jobs = await _context.Job
         .AsNoTracking()
         .OrderByDescending(job => job.CreatedAt)
         .ToListAsync(HttpContext.RequestAborted);
     return Ok(jobs.Select(JobResponseDto.FromJob).ToList());
    }

    [HttpGet("{id}")]
    async public Task<IActionResult> GetJobById(Guid id)
    {
        var job = await _context.Job.FirstOrDefaultAsync(j=> j.Id == id);
        if (job==null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job not found",
                Detail = $"No job exists with ID {id}. It may have been deleted.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(JobResponseDto.FromJob(job));

    }

    [HttpGet("{id}/report")]
    public async Task<IActionResult> DownloadReport(Guid id)
    {
        var job = await _context.Job
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id,
                HttpContext.RequestAborted);

        if (job is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job not found",
                Detail = $"No job exists with ID {id}. It may have been deleted.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (!job.ReportReady)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Final report is not ready",
                Detail = job.Status is JobStatus.Pending or JobStatus.Running
                    ? $"CodeForge is currently {JobResultSummary.FormatStage(job.ProcessingStage)}. Try again after processing reaches a terminal state."
                    : "Processing ended before the final report could be persisted.",
                Status = StatusCodes.Status409Conflict
            });
        }

        ReadOnlyMemory<byte>? dependencyGraph = null;
        ReadOnlyMemory<byte>? dotNetProjectGraph = null;
        if (job.GetDependencyAnalysis()?.GraphAvailable == true)
        {
            try
            {
                dependencyGraph = await _reportStore.GetDependencyGraphJpegAsync(
                    job.Id,
                    HttpContext.RequestAborted);
            }
            catch (ArtifactStorageException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not load the dependency graph for report {JobId}",
                    job.Id);
            }
        }

        if (job.GetDotNetArchitecture()?.GraphAvailable == true)
        {
            try
            {
                dotNetProjectGraph = await _reportStore.GetDotNetProjectGraphJpegAsync(job.Id, HttpContext.RequestAborted);
            }
            catch (ArtifactStorageException exception)
            {
                _logger.LogWarning(exception, "Could not load the .NET project graph for report {JobId}", job.Id);
            }
        }

        var pdf = JobReportPdfGenerator.Generate(job, dependencyGraph, dotNetProjectGraph);

        try
        {
            await _reportStore.SaveAsync(
                job.Id,
                pdf,
                HttpContext.RequestAborted);
        }
        catch (ReportStorageException exception)
        {
            _logger.LogError(
                exception,
                "Could not save the PDF report for job {JobId}",
                job.Id);

            return Problem(
                title: "Report storage is unavailable",
                detail: "The PDF was generated but could not be saved to Blob Storage.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return File(
            pdf,
            "application/pdf",
            $"codeforge-report-{job.Id:N}.pdf");
    }

    [HttpGet("{id}/dotnet-project-graph")]
    public async Task<IActionResult> GetDotNetProjectGraph(Guid id)
    {
        var job = await _context.Job.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, HttpContext.RequestAborted);
        if (job is null || job.GetDotNetArchitecture()?.GraphAvailable != true) return NotFound();
        try
        {
            var graph = await _reportStore.GetDotNetProjectGraphPngAsync(id, HttpContext.RequestAborted);
            return graph is null ? NotFound() : File(graph.Value.ToArray(), "image/png");
        }
        catch (ArtifactStorageException exception)
        {
            _logger.LogError(exception, "Could not load .NET project graph for job {JobId}", id);
            return Problem(title: ".NET project graph storage is unavailable", statusCode: 503);
        }
    }

    [HttpGet("{id}/dependency-graph")]
    public async Task<IActionResult> GetDependencyGraph(Guid id)
    {
        var job = await _context.Job
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id,
                HttpContext.RequestAborted);

        if (job is null)
        {
            return NotFound();
        }

        if (job.GetDependencyAnalysis()?.GraphAvailable != true)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Dependency graph unavailable",
                Detail = "This analysis does not have a rendered JavaScript/TypeScript dependency graph.",
                Status = StatusCodes.Status404NotFound
            });
        }

        try
        {
            var graph = await _reportStore.GetDependencyGraphPngAsync(
                job.Id,
                HttpContext.RequestAborted);
            return graph is null
                ? NotFound()
                : File(graph.Value.ToArray(), "image/png");
        }
        catch (ArtifactStorageException exception)
        {
            _logger.LogError(
                exception,
                "Could not load dependency graph for job {JobId}",
                job.Id);
            return Problem(
                title: "Dependency graph storage is unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] CreateJobDto createJobDto)
    {
      if (!JobValidationHelper.IsValidGitHubUrl(createJobDto.RepoUrl))
      {
          return BadRequest("RepoUrl must be a valid HTTPS GitHub repository URL.");
      }

      if (!JobValidationHelper.IsValidTitle(createJobDto.JobTitle))
      {
          return BadRequest("JobTitle cannot exceed 100 characters.");
      }

      if (!JobValidationHelper.IsValidDescription(createJobDto.JobDescription))
      {
          return BadRequest("JobDescription cannot exceed 500 characters.");
      }

      var job = new Job
      {
          RepoUrl = createJobDto.RepoUrl,
          JobTitle = string.IsNullOrWhiteSpace(createJobDto.JobTitle) ? null : createJobDto.JobTitle.Trim(),
          JobDescription = string.IsNullOrWhiteSpace(createJobDto.JobDescription) ? null : createJobDto.JobDescription.Trim(),
          RunTests = createJobDto.RunTests
      };

      var outboxjob = new OutboxMessage
      {
          JobId = job.Id,
          Job = job
      };

      await _context.Job.AddAsync(job);
      await _context.OutboxMessage.AddAsync(outboxjob);
      await _context.SaveChangesAsync();
      return Ok(JobResponseDto.FromJob(job));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateJob(Guid id, [FromBody] UpdateJobDto updateJobDto)
    {
        if (!JobValidationHelper.IsValidTitle(updateJobDto.JobTitle))
        {
            return BadRequest("JobTitle is required and cannot exceed 100 characters.");
        }

        if (!JobValidationHelper.IsValidDescription(updateJobDto.JobDescription))
        {
            return BadRequest("JobDescription is required and cannot exceed 500 characters.");
        }

        var job = await _context.Job.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound();
        }

        // RepoUrl and Id are intentionally not changed by this endpoint.
        job.JobTitle = updateJobDto.JobTitle;
        job.JobDescription = updateJobDto.JobDescription;
        job.Status = updateJobDto.Status;
        job.StartedAt = updateJobDto.StartedAt;
        job.CompletedAt = updateJobDto.CompletedAt;
        job.FailureReason = updateJobDto.Status == JobStatus.Failed
            ? updateJobDto.FailureReason
            : null;

        await _context.SaveChangesAsync();
        return Ok(JobResponseDto.FromJob(job));
    }

    [HttpPost("{id}/retry")]
    public async Task<IActionResult> RetryJob(Guid id)
    {
        var job = await _context.Job.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            HttpContext.RequestAborted);

        if (job is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job not found",
                Detail = $"No job exists with ID {id}. It may have been deleted.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (job.Status != JobStatus.Failed)
        {
            return Conflict("Only failed jobs can be retried.");
        }

        job.Status = JobStatus.Pending;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.FailureReason = null;
        job.ProcessingStage = ProcessingStage.Queued;
        job.AnalysisOutcome = null;
        job.ReportReady = false;
        job.SetTestSummary(JobTestSummary.NotRun());
        job.ClearStaticAnalysis();
        await _context.SaveChangesAsync(HttpContext.RequestAborted);

        // The reset is persisted before a fresh message is published so the worker
        // can claim this attempt as soon as it receives the message.
        await _producer.SendMessageAsync(id, Guid.NewGuid(), HttpContext.RequestAborted);

        return Ok(JobResponseDto.FromJob(job));
    }


    [HttpDelete("{id}")]
    async public Task<IActionResult> DeleteJob(Guid id)
    {
        var job = await _context.Job.FirstOrDefaultAsync(j=> j.Id == id);
        if (job==null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job not found",
                Detail = $"No job exists with ID {id}. It may have been deleted.",
                Status = StatusCodes.Status404NotFound
            });
        }
        
        _context.Job.Remove(job);
        await _context.SaveChangesAsync();
        return Ok(JobResponseDto.FromJob(job));
    }
}

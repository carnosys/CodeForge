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

        var pdf = JobReportPdfGenerator.Generate(job);

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

    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] CreateJobDto createJobDto)
    {
      if (!JobValidationHelper.IsValidGitHubUrl(createJobDto.RepoUrl))
      {
          return BadRequest("RepoUrl must be a valid HTTPS GitHub repository URL.");
      }

      if (!JobValidationHelper.IsValidTitle(createJobDto.JobTitle))
      {
          return BadRequest("JobTitle is required and cannot exceed 100 characters.");
      }

      if (!JobValidationHelper.IsValidDescription(createJobDto.JobDescription))
      {
          return BadRequest("JobDescription is required and cannot exceed 500 characters.");
      }

      var job = new Job
      {
          RepoUrl = createJobDto.RepoUrl,
          JobTitle = createJobDto.JobTitle,
          JobDescription = createJobDto.JobDescription
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
        var resetCount = await _context.Job
            .Where(job => job.Id == id && job.Status == JobStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, JobStatus.Pending)
                .SetProperty(job => job.StartedAt, (DateTime?)null)
                .SetProperty(job => job.CompletedAt, (DateTime?)null)
                .SetProperty(job => job.FailureReason, (string?)null),
                HttpContext.RequestAborted);

        if (resetCount == 0)
        {
            var exists = await _context.Job.AnyAsync(
                job => job.Id == id,
                HttpContext.RequestAborted);

            return exists
                ? Conflict("Only failed jobs can be retried.")
                : NotFound(new ProblemDetails
                {
                    Title = "Job not found",
                    Detail = $"No job exists with ID {id}. It may have been deleted.",
                    Status = StatusCodes.Status404NotFound
                });
        }

        // The reset is persisted before a fresh message is published so the worker
        // can claim this attempt as soon as it receives the message.
        await _producer.SendMessageAsync(id, Guid.NewGuid(), HttpContext.RequestAborted);

        var job = await _context.Job.AsNoTracking().SingleAsync(
            job => job.Id == id,
            HttpContext.RequestAborted);

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

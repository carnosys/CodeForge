using CodeForge.Dtos;
using CodeForge.Helpers;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;


[ApiController]
[Route("api/[controller]")]
public class JobController : ControllerBase
{
    private CodeForgeDbContext _context;
    public JobController(CodeForgeDbContext context)
    {
        _context = context;
    }

   [HttpGet]
    public async Task<IActionResult> GetJobs()
    {
     var Jobs = await _context.Job.ToListAsync();
     return Ok(Jobs);
    }

    [HttpGet("{id}")]
    async public Task<IActionResult> GetJobById(Guid id)
    {
        var job = await _context.Job.FirstOrDefaultAsync(j=> j.Id == id);
        if (job==null)
        {
            return NotFound();
        }

        return Ok(job);

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

      await _context.Job.AddAsync(job);
      await _context.SaveChangesAsync();
      return Ok(job);
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

        await _context.SaveChangesAsync();
        return Ok(job);
    }
   


    [HttpDelete("{id}")]
    async public Task<IActionResult> DeleteJob(Guid id)
    {
        var job = await _context.Job.FirstOrDefaultAsync(j=> j.Id == id);
        if (job==null)
        {
            return NotFound();
        }
        
        _context.Job.Remove(job);
        await _context.SaveChangesAsync();
        return Ok(job);
    }

 
}

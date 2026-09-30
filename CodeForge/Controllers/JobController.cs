using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models.Job;


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
        var job = _context.Job.FirstOrDefaultAsync(j=> j.Id == id);
        if (job==null)
        {
            return NotFound();
        }

        return Ok(job);

    }

    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] Job job)
    {
      await _context.Job.AddAsync(job);
      await _context.SaveChangesAsync();
      return Ok(job);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateJob(Guid id,[FromBody]  Job updatedJob)
    {
        var job = await _context.Job.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound();
        }

        // RepoUrl and Id are intentionally not changed by this endpoint.
        job.JobTitle = updatedJob.JobTitle;
        job.JobDescription = updatedJob.JobDescription;
        job.Status = updatedJob.Status;
        job.CreatedAt = updatedJob.CreatedAt;
        job.StartedAt = updatedJob.StartedAt;
        job.CompletedAt = updatedJob.CompletedAt;

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
using Microsoft.EntityFrameworkCore;
using Models.Job;

public class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorker(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {


        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();

            var job = await context.Job.FirstOrDefaultAsync(j => j.Status == JobStatus.Pending, stoppingToken);

            if (job == null)
            {
                await Task.Delay(5000, stoppingToken);
                continue;
            }

            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;

            await context.SaveChangesAsync(stoppingToken);

            try
            {

                await ProcessJob(job, stoppingToken);
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }

            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }

            catch (Exception)
            {
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }

        }
    }


    private async Task ProcessJob(Job job, CancellationToken stoppingToken)
    {
        //do something
        await Task.Delay(5000, stoppingToken);
    }
}


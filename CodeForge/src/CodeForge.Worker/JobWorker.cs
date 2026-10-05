using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Worker;

public class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RepositoryProcessor _repositoryProcessor;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(
        IServiceScopeFactory scopeFactory,
        RepositoryProcessor repositoryProcessor,
        ILogger<JobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _repositoryProcessor = repositoryProcessor;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {


        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();

            var job = await ClaimNextJobAsync(context, stoppingToken);

            if (job == null)
            {
                await Task.Delay(5000, stoppingToken);
                continue;
            }

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

            catch (Exception exception)
            {
                _logger.LogError(exception, "Job {JobId} failed", job.Id);
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }

        }
    }

    private static async Task<Job?> ClaimNextJobAsync(
        CodeForgeDbContext context,
        CancellationToken stoppingToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);

        var jobs = await context.Job
            .FromSqlInterpolated($$"""
                SELECT *
                FROM "Job"
                WHERE "Status" = {{(int)JobStatus.Pending}}
                ORDER BY "CreatedAt"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .ToListAsync(stoppingToken);

        var job = jobs.SingleOrDefault();

        if (job is null)
        {
            await transaction.RollbackAsync(stoppingToken);
            return null;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(stoppingToken);
        await transaction.CommitAsync(stoppingToken);

        return job;
    }


    private async Task ProcessJob(Job job, CancellationToken stoppingToken)
    {
        await _repositoryProcessor.ProcessRepoAsync(job.RepoUrl, stoppingToken);
    }
}

using CodeForge.Infrastructure.Models;

namespace CodeForge.Worker;

public static class JobCompletion
{
    public static async Task FinalizeAsync(
        Job job,
        RepositoryProcessingResult result,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Completed;
        job.ProcessingStage = ProcessingStage.Completed;
        job.AnalysisOutcome = JobAnalysisOutcomeResolver.Resolve(result);
        job.ReportReady = true;
        job.CompletedAt = DateTime.UtcNow;
        job.FailureReason = result.BuildFailure;
        try
        {
            await persist(cancellationToken);
        }
        catch
        {
            job.Status = JobStatus.Running;
            job.ProcessingStage = ProcessingStage.Finalizing;
            job.AnalysisOutcome = null;
            job.ReportReady = false;
            job.CompletedAt = null;
            throw;
        }
    }
}

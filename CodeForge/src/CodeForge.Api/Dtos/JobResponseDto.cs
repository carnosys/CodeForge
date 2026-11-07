using CodeForge.Infrastructure.Models;

namespace CodeForge.Dtos;

public sealed class JobResponseDto
{
    public Guid Id { get; init; }

    public required string RepoUrl { get; init; }

    public string? JobTitle { get; init; }

    public string? JobDescription { get; init; }

    public JobStatus Status { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public string? FailureReason { get; init; }

    public static JobResponseDto FromJob(Job job)
    {
        return new JobResponseDto
        {
            Id = job.Id,
            RepoUrl = job.RepoUrl,
            JobTitle = job.JobTitle,
            JobDescription = job.JobDescription,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            FailureReason = job.FailureReason
        };
    }
}

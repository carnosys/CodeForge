using CodeForge.Infrastructure.Models;

namespace CodeForge.Dtos;

public class UpdateJobDto
{
    public string? JobTitle { get; set; }

    public string? JobDescription { get; set; }

    public JobStatus Status { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}



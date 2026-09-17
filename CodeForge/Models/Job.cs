namespace Models.Job;
public class Job
{
    public Guid Id {get; set;} = Guid.NewGuid();
    
    public required string RepoUrl {get; set;} 
    public string? JobTitle {get; set;} 
    public string? JobDescription {get; set;}

    public JobStatus Status {get; set;} = JobStatus.Pending;

    public DateTime CreatedAt {get; set;} = DateTime.UtcNow;

    public DateTime? StartedAt {get; set;}

    public DateTime? CompletedAt {get; set;}
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
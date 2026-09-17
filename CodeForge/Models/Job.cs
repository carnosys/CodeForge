
public class Job
{
    public Guid Id {get; set;} = Guid.NewGuid();
    public required string JobTitle {get; set;} 

    public string JobDescription {get; set;} = "";

    public JobStatus Status {get; set;} = JobStatus.Pending;

    public DateTime CreatedAt {get; set;} = DateTime.Now;

    public DateTime ProcessingStartTime {get; set;}

    public DateTime JobEndTime {get; set;}
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
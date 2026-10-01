using System.ComponentModel.DataAnnotations;

namespace Models.Job;


public class GitHubRepoUrlAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if(value is not string url)
        {
            return false;
        }

        if(!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if(uri.Scheme != "https")
        {
            return false;
        }

        if (uri.Host!="github.com")
        {
            return false;
        }

        var cleanPath = uri.AbsolutePath.Trim('/');

        var parts = cleanPath.Split('/');

        return parts.Length == 2; 
    }
}
public class Job
{
    public Guid Id {get; set;} = Guid.NewGuid();
    
    [GitHubRepoUrl]
    public required string RepoUrl {get; set;}

    [MaxLength(100)] 
    public string? JobTitle {get; set;}

    [MaxLength(500)] 
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
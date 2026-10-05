namespace CodeForge.Dtos;

public class CreateJobDto
{
    public required string RepoUrl { get; set; }

    public string? JobTitle { get; set; }

    public string? JobDescription { get; set; }
}


using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CodeForge.Infrastructure.Models;


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
    private static readonly JsonSerializerOptions AnalysisJsonOptions = new(JsonSerializerDefaults.Web);

    public Guid Id {get; set;} = Guid.NewGuid();
    
    [GitHubRepoUrl]
    public required string RepoUrl {get; set;}

    [MaxLength(100)] 
    public string? JobTitle {get; set;}

    [MaxLength(500)] 
    public string? JobDescription {get; set;}

    public OutboxMessage? OutboxMessage {get; set;}

    public bool RunTests { get; set; }

    public JobTestSummary? TestSummary { get; set; }

    public string? ComplexityAnalysisJson { get; set; }

    public string? DependencyAnalysisJson { get; set; }

    public string? BuildFindingsJson { get; set; }

    public string? CoverageAnalysisJson { get; set; }

    public string? DotNetArchitectureJson { get; set; }

    public ProcessingStage ProcessingStage { get; set; } = ProcessingStage.Queued;

    public JobAnalysisOutcome? AnalysisOutcome { get; set; }

    public bool ReportReady { get; set; }

    [MaxLength(64)]
    public string? RepositoryCommitSha { get; set; }

    [MaxLength(200)]
    public string? WorkerVersion { get; set; }

    public void SetTestSummary(JobTestSummary summary)
    {
        TestSummary ??= new JobTestSummary();
        TestSummary.Outcome = summary.Outcome;
        TestSummary.Executed = summary.Executed;
        TestSummary.Passed = summary.Passed;
        TestSummary.Failed = summary.Failed;
        TestSummary.Skipped = summary.Skipped;
        TestSummary.IsPartial = summary.IsPartial;
        TestSummary.SkipReason = summary.SkipReason;
    }

    public void SetComplexityAnalysis(CodeComplexityAnalysis analysis)
    {
        ComplexityAnalysisJson = JsonSerializer.Serialize(analysis, AnalysisJsonOptions);
    }

    public CodeComplexityAnalysis? GetComplexityAnalysis()
    {
        return ComplexityAnalysisJson is null
            ? null
            : JsonSerializer.Deserialize<CodeComplexityAnalysis>(
                ComplexityAnalysisJson,
                AnalysisJsonOptions);
    }

    public void SetDependencyAnalysis(JavaScriptDependencyAnalysis analysis)
    {
        DependencyAnalysisJson = JsonSerializer.Serialize(analysis, AnalysisJsonOptions);
    }

    public JavaScriptDependencyAnalysis? GetDependencyAnalysis()
    {
        return DependencyAnalysisJson is null
            ? null
            : JsonSerializer.Deserialize<JavaScriptDependencyAnalysis>(
                DependencyAnalysisJson,
                AnalysisJsonOptions);
    }

    public void SetBuildFindings(BuildFindingsAnalysis analysis)
    {
        BuildFindingsJson = JsonSerializer.Serialize(analysis, AnalysisJsonOptions);
    }

    public BuildFindingsAnalysis? GetBuildFindings()
    {
        return BuildFindingsJson is null
            ? null
            : JsonSerializer.Deserialize<BuildFindingsAnalysis>(
                BuildFindingsJson,
                AnalysisJsonOptions);
    }

    public void SetCoverageAnalysis(JobCoverageAnalysis analysis)
    {
        CoverageAnalysisJson = JsonSerializer.Serialize(analysis, AnalysisJsonOptions);
    }

    public JobCoverageAnalysis? GetCoverageAnalysis()
    {
        return CoverageAnalysisJson is null
            ? null
            : JsonSerializer.Deserialize<JobCoverageAnalysis>(CoverageAnalysisJson, AnalysisJsonOptions);
    }

    public void SetDotNetArchitecture(DotNetArchitectureAnalysis analysis)
    {
        DotNetArchitectureJson = JsonSerializer.Serialize(analysis, AnalysisJsonOptions);
    }

    public DotNetArchitectureAnalysis? GetDotNetArchitecture()
    {
        return DotNetArchitectureJson is null
            ? null
            : JsonSerializer.Deserialize<DotNetArchitectureAnalysis>(DotNetArchitectureJson, AnalysisJsonOptions);
    }

    public void ClearStaticAnalysis()
    {
        ComplexityAnalysisJson = null;
        DependencyAnalysisJson = null;
        BuildFindingsJson = null;
        CoverageAnalysisJson = null;
        DotNetArchitectureJson = null;
        AnalysisOutcome = null;
        ReportReady = false;
        RepositoryCommitSha = null;
        WorkerVersion = null;
    }

    public JobStatus Status {get; set;} = JobStatus.Pending;

    public DateTime CreatedAt {get; set;} = DateTime.UtcNow;

    public DateTime? StartedAt {get; set;}

    public DateTime? CompletedAt {get; set;}

    [MaxLength(4000)]
    public string? FailureReason {get; set;}
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum ProcessingStage
{
    Queued,
    Cloning,
    StaticAnalysis,
    ProjectDiscovery,
    Building,
    Testing,
    Finalizing,
    Completed,
    Failed
}

public enum JobAnalysisOutcome
{
    Completed,
    CompletedWithAnalysisErrors,
    BuildFailed,
    InfrastructureFailed
}

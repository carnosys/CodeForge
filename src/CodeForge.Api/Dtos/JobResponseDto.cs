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

    public ProcessingStage ProcessingStage { get; init; }

    public JobAnalysisOutcome? AnalysisOutcome { get; init; }

    public bool ReportReady { get; init; }

    public string? RepositoryCommitSha { get; init; }

    public string? WorkerVersion { get; init; }

    public required JobResultSummary ResultSummary { get; init; }

    public bool RunTests { get; init; }

    public JobTestSummaryDto? TestResults { get; init; }

    public CoverageAnalysisDto? Coverage { get; init; }

    public BuildFindingsDto? BuildFindings { get; init; }

    public CodeComplexityAnalysis? CodeComplexity { get; init; }

    public JavaScriptDependencyAnalysis? JavaScriptDependencies { get; init; }

    public DotNetArchitectureAnalysis? DotNetArchitecture { get; init; }

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
            FailureReason = job.FailureReason,
            ProcessingStage = job.ProcessingStage,
            AnalysisOutcome = job.AnalysisOutcome,
            ReportReady = job.ReportReady,
            RepositoryCommitSha = job.RepositoryCommitSha,
            WorkerVersion = job.WorkerVersion,
            ResultSummary = JobResultSummary.FromJob(job),
            RunTests = job.RunTests,
            TestResults = job.TestSummary is null
                ? null
                : JobTestSummaryDto.FromSummary(job.TestSummary),
            Coverage = CoverageAnalysisDto.FromAnalysis(job.GetCoverageAnalysis()),
            BuildFindings = BuildFindingsDto.FromAnalysis(job.GetBuildFindings()),
            CodeComplexity = job.GetComplexityAnalysis(),
            JavaScriptDependencies = job.GetDependencyAnalysis(),
            DotNetArchitecture = job.GetDotNetArchitecture()
        };
    }
}

public sealed class CoverageAnalysisDto
{
    public CoverageOutcome Outcome { get; init; }
    public int? CoveredLines { get; init; }
    public int? MeasurableLines { get; init; }
    public decimal? LinePercentage { get; init; }
    public int? CoveredBranches { get; init; }
    public int? MeasurableBranches { get; init; }
    public decimal? BranchPercentage { get; init; }
    public required IReadOnlyList<FileCoverageDto> Files { get; init; }
    public required IReadOnlyList<CoverageReviewPriority> ReviewPriorities { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
    public required string CollectionProfile { get; init; }
    public bool CheckoutModified { get; init; }

    public static CoverageAnalysisDto? FromAnalysis(JobCoverageAnalysis? analysis)
    {
        if (analysis is null) return null;
        return new CoverageAnalysisDto
        {
            Outcome = analysis.Outcome,
            CoveredLines = analysis.CoveredLines,
            MeasurableLines = analysis.MeasurableLines,
            LinePercentage = Percentage(analysis.CoveredLines, analysis.MeasurableLines),
            CoveredBranches = analysis.CoveredBranches,
            MeasurableBranches = analysis.MeasurableBranches,
            BranchPercentage = Percentage(analysis.CoveredBranches, analysis.MeasurableBranches),
            Files = analysis.Files.Select(FileCoverageDto.FromFile).ToArray(),
            ReviewPriorities = analysis.ReviewPriorities,
            Limitations = analysis.Limitations,
            CollectionProfile = analysis.CollectionProfile,
            CheckoutModified = analysis.CheckoutModified
        };
    }

    private static decimal? Percentage(int? covered, int? measurable) =>
        covered is not null && measurable > 0
            ? Math.Round(covered.Value * 100m / measurable.Value, 1)
            : null;
}

public sealed class FileCoverageDto
{
    public required string RelativePath { get; init; }
    public int CoveredLines { get; init; }
    public int MeasurableLines { get; init; }
    public decimal? LinePercentage { get; init; }
    public int? CoveredBranches { get; init; }
    public int? MeasurableBranches { get; init; }
    public decimal? BranchPercentage { get; init; }
    public required IReadOnlyList<int> UncoveredLines { get; init; }

    public static FileCoverageDto FromFile(FileCoverage file) => new()
    {
        RelativePath = file.RelativePath,
        CoveredLines = file.CoveredLines,
        MeasurableLines = file.MeasurableLines,
        LinePercentage = file.MeasurableLines > 0
            ? Math.Round(file.CoveredLines * 100m / file.MeasurableLines, 1)
            : null,
        CoveredBranches = file.CoveredBranches,
        MeasurableBranches = file.MeasurableBranches,
        BranchPercentage = file.MeasurableBranches > 0
            ? Math.Round((file.CoveredBranches ?? 0) * 100m / file.MeasurableBranches.Value, 1)
            : null,
        UncoveredLines = file.UncoveredLines
    };
}

public sealed class BuildFindingsDto
{
    public BuildFindingsOutcome Outcome { get; init; }

    public bool? BuildSucceeded { get; init; }

    public string? SdkVersion { get; init; }

    public required string BuildConfiguration { get; init; }

    public required string AnalysisProfile { get; init; }

    public bool AdditionalRulesEnabled { get; init; }

    public required IReadOnlyDictionary<string, int> SeverityCounts { get; init; }

    public required IReadOnlyList<BuildFindingDto> Findings { get; init; }

    public static BuildFindingsDto? FromAnalysis(BuildFindingsAnalysis? analysis)
    {
        if (analysis is null)
        {
            return null;
        }

        var counts = analysis.Findings
            .GroupBy(finding => finding.ReportedSeverity, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new BuildFindingsDto
        {
            Outcome = analysis.Outcome,
            BuildSucceeded = analysis.BuildSucceeded,
            SdkVersion = analysis.SdkVersion,
            BuildConfiguration = analysis.BuildConfiguration,
            AnalysisProfile = analysis.AnalysisProfile,
            AdditionalRulesEnabled = analysis.AdditionalRulesEnabled,
            SeverityCounts = counts,
            Findings = analysis.Findings.Select(BuildFindingDto.FromFinding).ToArray()
        };
    }
}

public sealed class BuildFindingDto
{
    public required string RuleId { get; init; }

    public required string ReportedSeverity { get; init; }

    public required string Message { get; init; }

    public required string Explanation { get; init; }

    public string? SuggestedAction { get; init; }

    public string? RelativePath { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public string? HelpLink { get; init; }

    public static BuildFindingDto FromFinding(BuildFinding finding)
    {
        var guidance = BuildFindingGuidance.Get(finding.RuleId);
        return new BuildFindingDto
        {
            RuleId = finding.RuleId,
            ReportedSeverity = finding.ReportedSeverity,
            Message = finding.Message,
            Explanation = guidance?.Explanation ?? finding.Message,
            SuggestedAction = guidance?.SuggestedAction,
            RelativePath = finding.RelativePath,
            Line = finding.Line,
            Column = finding.Column,
            HelpLink = finding.HelpLink
        };
    }
}

public sealed class JobTestSummaryDto
{
    public TestRunOutcome Outcome { get; init; }

    public int? Executed { get; init; }

    public int? Passed { get; init; }

    public int? Failed { get; init; }

    public int? Skipped { get; init; }

    public bool IsPartial { get; init; }

    public TestSkipReason SkipReason { get; init; }

    public static JobTestSummaryDto FromSummary(JobTestSummary summary)
    {
        return new JobTestSummaryDto
        {
            Outcome = summary.Outcome,
            Executed = summary.Executed,
            Passed = summary.Passed,
            Failed = summary.Failed,
            Skipped = summary.Skipped,
            IsPartial = summary.IsPartial,
            SkipReason = summary.SkipReason
        };
    }
}

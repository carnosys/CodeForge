namespace CodeForge.Infrastructure.Models;

public enum CoverageOutcome
{
    Completed,
    Partial,
    NotRun,
    NoTestProjects,
    UnsupportedCollector,
    Unavailable,
    NotApplicable
}

public sealed class JobCoverageAnalysis
{
    public CoverageOutcome Outcome { get; init; }

    public int? CoveredLines { get; init; }

    public int? MeasurableLines { get; init; }

    public int? CoveredBranches { get; init; }

    public int? MeasurableBranches { get; init; }

    public IReadOnlyList<FileCoverage> Files { get; init; } = [];

    public IReadOnlyList<CoverageReviewPriority> ReviewPriorities { get; init; } = [];

    public IReadOnlyList<string> Limitations { get; init; } = [];

    public string CollectionProfile { get; init; } =
        "VSTest XPlat Code Coverage using the repository's coverlet.collector package and a worker-owned temporary runsettings file.";

    public bool CheckoutModified { get; init; }

    public static JobCoverageAnalysis NotRun(string reason) => new()
    {
        Outcome = CoverageOutcome.NotRun,
        Limitations = [reason]
    };
}

public sealed class FileCoverage
{
    public required string RelativePath { get; init; }

    public int CoveredLines { get; init; }

    public int MeasurableLines { get; init; }

    public int? CoveredBranches { get; init; }

    public int? MeasurableBranches { get; init; }

    public IReadOnlyList<int> UncoveredLines { get; init; } = [];
}

public sealed class CoverageReviewPriority
{
    public required string FunctionName { get; init; }

    public required string RelativePath { get; init; }

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public int Complexity { get; init; }

    public IReadOnlyList<int> UncoveredLines { get; init; } = [];
}

namespace CodeForge.Infrastructure.Models;

public enum BuildFindingsOutcome
{
    Completed,
    NotApplicable,
    Partial,
    Unsupported,
    Unavailable
}

public sealed class BuildFindingsAnalysis
{
    public BuildFindingsOutcome Outcome { get; init; }

    public bool? BuildSucceeded { get; init; }

    public string? SdkVersion { get; init; }

    public required string BuildConfiguration { get; init; }

    public required string AnalysisProfile { get; init; }

    public bool AdditionalRulesEnabled { get; init; }

    public IReadOnlyList<BuildFinding> Findings { get; init; } = [];
}

public sealed class BuildFinding
{
    public required string RuleId { get; init; }

    public required string ReportedSeverity { get; init; }

    public required string Message { get; init; }

    public string? RelativePath { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public string? HelpLink { get; init; }
}

public sealed record BuildFindingGuidanceInfo(
    string Explanation,
    string SuggestedAction);

public static class BuildFindingGuidance
{
    public static BuildFindingGuidanceInfo? Get(string ruleId)
    {
        return ruleId.ToUpperInvariant() switch
        {
            "CS0168" => new(
                "A local variable was declared but is never used.",
                "Remove the variable, or use it if it represents required work."),
            "CS0618" => new(
                "The code uses an API that its author marked obsolete.",
                "Follow the replacement guidance in the diagnostic and migrate away from the obsolete API."),
            "CS8602" => new(
                "Nullable analysis found a value that may be null when it is dereferenced.",
                "Add a null check, improve nullable annotations, or restructure the flow so non-null state is proven."),
            "CS8618" => new(
                "A non-nullable member may still be null when construction finishes.",
                "Initialize the member, make it nullable, or require callers to provide it during construction."),
            "CA1062" => new(
                "A publicly callable method uses a reference argument without validating it for null.",
                "Validate the argument at the public boundary, for example with ArgumentNullException.ThrowIfNull."),
            "CA1822" => new(
                "The member does not access instance state.",
                "Mark the member static when that matches the intended API design."),
            "CA2000" => new(
                "A disposable object may leave scope without being disposed.",
                "Transfer ownership explicitly or dispose it with a using statement or try/finally block."),
            _ => null
        };
    }
}

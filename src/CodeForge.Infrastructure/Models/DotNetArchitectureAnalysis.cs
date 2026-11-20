namespace CodeForge.Infrastructure.Models;

public enum ProjectReferenceDiscoveryMode
{
    Evaluated,
    LiteralFallback,
    Mixed
}

public enum ArchitectureAnalysisOutcome
{
    Completed,
    Partial,
    NotApplicable,
    ToolError
}

public sealed class DotNetArchitectureAnalysis
{
    public ArchitectureAnalysisOutcome Outcome { get; init; }
    public ProjectReferenceDiscoveryMode DiscoveryMode { get; init; }
    public IReadOnlyList<DotNetProjectNode> Projects { get; init; } = [];
    public IReadOnlyList<DotNetProjectReference> References { get; init; } = [];
    public bool RulesConfigured { get; init; }
    public IReadOnlyList<ArchitectureRule> Rules { get; init; } = [];
    public IReadOnlyList<ArchitectureViolation> Violations { get; init; } = [];
    public IReadOnlyList<string> ConfigurationErrors { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public bool GraphAvailable { get; init; }
    public bool GraphTruncated { get; init; }
}

public sealed class DotNetProjectNode
{
    public required string Path { get; init; }
}

public sealed class DotNetProjectReference
{
    public required string Source { get; init; }
    public required string Target { get; init; }
}

public sealed class ArchitectureRule
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public string? Description { get; init; }
}

public sealed class ArchitectureViolation
{
    public required string RuleId { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public string? Description { get; init; }
}

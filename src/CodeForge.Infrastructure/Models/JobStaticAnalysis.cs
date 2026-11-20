namespace CodeForge.Infrastructure.Models;

public enum AnalyzerOutcome
{
    Completed,
    NoSource,
    Partial,
    TimedOut,
    ToolError,
    ToolUnavailable,
    ExecutionFailed,
    MalformedOutput
}

public sealed class CodeComplexityAnalysis
{
    public const int ComplexityThreshold = 15;

    public required LanguageComplexitySummary CSharp { get; init; }

    public required LanguageComplexitySummary JavaScriptTypeScript { get; init; }
}

public sealed class LanguageComplexitySummary
{
    public AnalyzerOutcome Outcome { get; init; }

    public int? FunctionsAnalyzed { get; init; }

    public int? FunctionsOverThreshold { get; init; }

    public IReadOnlyList<ComplexityFinding> HighestComplexityFunctions { get; init; } = [];

    public IReadOnlyList<ComplexityFinding> FunctionsOverThresholdDetails { get; init; } = [];

    public AnalyzerExecutionDetails? Execution { get; init; }
}

public sealed class ComplexityFinding
{
    public required string FunctionName { get; init; }

    public required string RelativePath { get; init; }

    public int Line { get; init; }

    public int EndLine { get; init; }

    public int Score { get; init; }
}

public sealed class JavaScriptDependencyAnalysis
{
    public AnalyzerOutcome Outcome { get; init; }

    public int SourceFileCount { get; init; }

    public int? ModuleCount { get; init; }

    public int? DependencyCount { get; init; }

    public int? CycleCount { get; init; }

    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; } = [];

    public bool MayBeIncomplete { get; init; }

    public bool GraphAvailable { get; init; }

    public AnalyzerExecutionDetails? Execution { get; init; }
}

public sealed class AnalyzerExecutionDetails
{
    public required string Tool { get; init; }
    public string? Version { get; init; }
    public long DurationMilliseconds { get; init; }
    public int? ExitCode { get; init; }
    public string? ErrorReason { get; init; }
}

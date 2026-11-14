using CodeForge.Infrastructure.Models;

namespace CodeForge.Worker;

public static class JobAnalysisOutcomeResolver
{
    public static JobAnalysisOutcome Resolve(RepositoryProcessingResult result)
    {
        if (result.BuildFailure is not null) return JobAnalysisOutcome.BuildFailed;

        var analyzerError = IsError(result.Complexity.CSharp.Outcome) ||
                            IsError(result.Complexity.JavaScriptTypeScript.Outcome) ||
                            IsError(result.Dependencies.Outcome) ||
                            result.BuildFindings.Outcome is BuildFindingsOutcome.Partial or
                                BuildFindingsOutcome.Unsupported or BuildFindingsOutcome.Unavailable ||
                            result.Architecture.Outcome is ArchitectureAnalysisOutcome.Partial or ArchitectureAnalysisOutcome.ToolError ||
                            result.Tests.Outcome is TestRunOutcome.ExecutionError or TestRunOutcome.TimedOut ||
                            result.Coverage.Outcome is CoverageOutcome.Partial or CoverageOutcome.Unavailable;
        return analyzerError
            ? JobAnalysisOutcome.CompletedWithAnalysisErrors
            : JobAnalysisOutcome.Completed;
    }

    private static bool IsError(AnalyzerOutcome outcome) => outcome is
        AnalyzerOutcome.Partial or
        AnalyzerOutcome.TimedOut or
        AnalyzerOutcome.ToolError or
        AnalyzerOutcome.ToolUnavailable or
        AnalyzerOutcome.ExecutionFailed or
        AnalyzerOutcome.MalformedOutput;
}

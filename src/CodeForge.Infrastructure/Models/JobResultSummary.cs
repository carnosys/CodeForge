namespace CodeForge.Infrastructure.Models;

public sealed record JobResultSummary(
    string Label,
    string Description,
    IReadOnlyList<string> FailedStages,
    IReadOnlyList<string> SkippedStages)
{
    public static JobResultSummary FromJob(Job job)
    {
        var failed = new List<string>();
        var skipped = new List<string>();
        AddAnalyzer("C# complexity", job.GetComplexityAnalysis()?.CSharp, failed, skipped);
        AddAnalyzer("JavaScript/TypeScript complexity", job.GetComplexityAnalysis()?.JavaScriptTypeScript, failed, skipped);
        AddDependency(job.GetDependencyAnalysis(), failed, skipped);

        var buildFindings = job.GetBuildFindings();
        if (buildFindings?.Outcome is BuildFindingsOutcome.Partial or BuildFindingsOutcome.Unsupported or BuildFindingsOutcome.Unavailable)
            failed.Add($"Build diagnostics: structured collection was {buildFindings.Outcome.ToString().ToLowerInvariant()}.");
        else if (buildFindings?.Outcome == BuildFindingsOutcome.NotApplicable)
            skipped.Add("Build diagnostics not applicable: no .NET project was found.");

        var architecture = job.GetDotNetArchitecture();
        if (architecture?.Outcome is ArchitectureAnalysisOutcome.Partial or ArchitectureAnalysisOutcome.ToolError)
            failed.Add(".NET architecture analysis did not complete fully.");
        if (architecture?.Outcome == ArchitectureAnalysisOutcome.NotApplicable)
            skipped.Add(".NET project architecture not applicable: no .NET project was found.");
        else if (architecture is not null && !architecture.RulesConfigured)
            skipped.Add("Skipped: no architecture rules configuration supplied.");

        if (job.Status is JobStatus.Completed or JobStatus.Failed)
        {
            if (job.TestSummary?.Outcome == TestRunOutcome.NotRun)
                skipped.Add(TestSkipMessage(job.TestSummary.SkipReason));
            else if (job.TestSummary?.Outcome is TestRunOutcome.ExecutionError or TestRunOutcome.TimedOut)
                failed.Add(".NET test execution did not complete successfully.");

            var coverage = job.GetCoverageAnalysis();
            if (coverage?.Outcome == CoverageOutcome.NotRun)
                skipped.Add(coverage.Limitations.FirstOrDefault() ?? "Coverage skipped because tests were not run.");
            else if (coverage?.Outcome is CoverageOutcome.Partial or CoverageOutcome.Unavailable)
                failed.Add(coverage.Limitations.FirstOrDefault() ?? "Coverage collection did not complete successfully.");
            else if (coverage?.Outcome == CoverageOutcome.UnsupportedCollector)
                skipped.Add(coverage.Limitations.FirstOrDefault() ?? "Coverage collector unsupported.");
        }

        var label = job.AnalysisOutcome switch
        {
            JobAnalysisOutcome.Completed => "Completed",
            JobAnalysisOutcome.CompletedWithAnalysisErrors => "Completed with analysis errors",
            JobAnalysisOutcome.BuildFailed => "Completed with build failure",
            JobAnalysisOutcome.InfrastructureFailed => "Processing failed",
            _ when job.Status == JobStatus.Running => "Processing",
            _ when job.Status == JobStatus.Pending => "Queued",
            _ when job.Status == JobStatus.Failed => "Processing failed",
            _ => "Completed (legacy report)"
        };
        var description = job.AnalysisOutcome switch
        {
            JobAnalysisOutcome.Completed => "All scheduled stages succeeded or were explicitly skipped or not applicable.",
            JobAnalysisOutcome.CompletedWithAnalysisErrors => "Processing finished and successful results were preserved, but one or more analysis stages failed or were incomplete.",
            JobAnalysisOutcome.BuildFailed => "Repository processing finished, but the .NET build failed. Static analysis results that completed are still available.",
            JobAnalysisOutcome.InfrastructureFailed => job.FailureReason ?? "CodeForge could not finish processing because of an infrastructure failure.",
            _ when job.Status == JobStatus.Running => $"Current stage: {FormatStage(job.ProcessingStage)}.",
            _ when job.Status == JobStatus.Pending => "Waiting for a worker.",
            _ => "This older job predates structured lifecycle reporting."
        };
        return new JobResultSummary(label, description, failed.Distinct().ToArray(), skipped.Distinct().ToArray());
    }

    public static string FormatStage(ProcessingStage stage) => stage switch
    {
        ProcessingStage.StaticAnalysis => "running static analyzers",
        ProcessingStage.ProjectDiscovery => "discovering projects",
        ProcessingStage.Building => "building and collecting diagnostics",
        ProcessingStage.Testing => "running trusted tests and coverage",
        ProcessingStage.Finalizing => "persisting the final report",
        ProcessingStage.Cloning => "cloning the repository",
        ProcessingStage.Completed => "complete",
        ProcessingStage.Failed => "failed",
        _ => "waiting for a worker"
    };

    private static void AddAnalyzer(string label, LanguageComplexitySummary? summary, ICollection<string> failed, ICollection<string> skipped)
    {
        if (summary is null) return;
        if (summary.Outcome == AnalyzerOutcome.NoSource) skipped.Add($"{label} not applicable: no supported source files found.");
        else if (summary.Outcome is not (AnalyzerOutcome.Completed))
            failed.Add($"{label}: {summary.Execution?.ErrorReason ?? "analysis did not complete fully."}");
    }

    private static void AddDependency(JavaScriptDependencyAnalysis? analysis, ICollection<string> failed, ICollection<string> skipped)
    {
        if (analysis is null) return;
        if (analysis.Outcome == AnalyzerOutcome.NoSource) skipped.Add("JavaScript/TypeScript dependencies not applicable: no supported source files found.");
        else if (analysis.Outcome is not AnalyzerOutcome.Completed)
            failed.Add($"JavaScript/TypeScript dependencies: {analysis.Execution?.ErrorReason ?? "analysis did not complete fully."}");
    }

    public static string TestSkipMessage(TestSkipReason reason) => reason switch
    {
        TestSkipReason.TrustedRepositoryOptInMissing => "Tests skipped: this job was not opted in as a trusted repository.",
        TestSkipReason.WorkerFeatureFlagDisabled => "Tests skipped: the worker test-execution feature flag is disabled.",
        TestSkipReason.BothPrerequisitesMissing => "Tests skipped: trusted-repository opt-in and the worker feature flag are both missing.",
        TestSkipReason.BuildFailed => "Tests skipped: the repository build failed.",
        TestSkipReason.ProjectDiscoveryFailed => "Tests skipped: .NET project discovery failed.",
        _ => "Tests skipped: execution prerequisites were not met."
    };
}

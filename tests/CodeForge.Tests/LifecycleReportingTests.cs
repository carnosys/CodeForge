using System.Text;
using CodeForge.Api.Reports;
using CodeForge.Dtos;
using CodeForge.Helpers;
using CodeForge.Infrastructure.Models;
using CodeForge.Worker;
using Xunit;

namespace CodeForge.Tests;

public sealed class LifecycleReportingTests
{
    [Fact]
    public void RunningAnalyzerDoesNotExposeFinalReport()
    {
        var job = JobWithDefaults();
        job.Status = JobStatus.Running;
        job.ProcessingStage = ProcessingStage.StaticAnalysis;

        var response = JobResponseDto.FromJob(job);

        Assert.False(response.ReportReady);
        Assert.Equal("Processing", response.ResultSummary.Label);
        Assert.Contains("static analyzers", response.ResultSummary.Description);
        Assert.Empty(response.ResultSummary.SkippedStages);
    }

    [Fact]
    public void BuildNameAndDescriptionAreOptionalMetadata()
    {
        Assert.True(JobValidationHelper.IsValidTitle(null));
        Assert.True(JobValidationHelper.IsValidDescription(""));
        Assert.False(JobValidationHelper.IsValidTitle(new string('x', 101)));
        Assert.False(JobValidationHelper.IsValidDescription(new string('x', 501)));
    }

    [Fact]
    public void AnalyzerFailurePreservesOtherResultsAndProducesAccurateSummary()
    {
        var job = JobWithDefaults();
        job.Status = JobStatus.Completed;
        job.AnalysisOutcome = JobAnalysisOutcome.CompletedWithAnalysisErrors;
        job.ReportReady = true;
        job.SetComplexityAnalysis(new CodeComplexityAnalysis
        {
            CSharp = new LanguageComplexitySummary
            {
                Outcome = AnalyzerOutcome.ToolUnavailable,
                Execution = new AnalyzerExecutionDetails { Tool = "lizard", DurationMilliseconds = 2, ErrorReason = "lizard is missing" }
            },
            JavaScriptTypeScript = new LanguageComplexitySummary { Outcome = AnalyzerOutcome.Completed, FunctionsAnalyzed = 3, FunctionsOverThreshold = 0 }
        });

        var summary = JobResultSummary.FromJob(job);

        Assert.Equal("Completed with analysis errors", summary.Label);
        Assert.Contains(summary.FailedStages, value => value.Contains("C# complexity") && value.Contains("lizard is missing"));
        Assert.DoesNotContain(summary.FailedStages, value => value.Contains("JavaScript/TypeScript complexity"));
    }

    [Fact]
    public void NoApplicableFilesIsNotAnAnalyzerFailure()
    {
        var job = JobWithDefaults();
        job.Status = JobStatus.Completed;
        job.AnalysisOutcome = JobAnalysisOutcome.Completed;
        job.ReportReady = true;
        job.SetComplexityAnalysis(new CodeComplexityAnalysis
        {
            CSharp = new LanguageComplexitySummary { Outcome = AnalyzerOutcome.NoSource, FunctionsAnalyzed = 0, FunctionsOverThreshold = 0 },
            JavaScriptTypeScript = new LanguageComplexitySummary { Outcome = AnalyzerOutcome.NoSource, FunctionsAnalyzed = 0, FunctionsOverThreshold = 0 }
        });

        var summary = JobResultSummary.FromJob(job);

        Assert.Empty(summary.FailedStages);
        Assert.Contains(summary.SkippedStages, value => value.Contains("no supported source files"));
    }

    [Theory]
    [InlineData(TestSkipReason.TrustedRepositoryOptInMissing, "not opted in")]
    [InlineData(TestSkipReason.WorkerFeatureFlagDisabled, "feature flag")]
    [InlineData(TestSkipReason.BothPrerequisitesMissing, "both missing")]
    [InlineData(TestSkipReason.BuildFailed, "build failed")]
    [InlineData(TestSkipReason.ProjectDiscoveryFailed, "project discovery failed")]
    public void TestSkipReasonsAreSpecific(TestSkipReason reason, string expected) =>
        Assert.Contains(expected, JobResultSummary.TestSkipMessage(reason), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task PersistenceFailureCannotLeaveJobFalselyCompleted()
    {
        var job = JobWithDefaults();
        job.Status = JobStatus.Running;
        var result = ResultWithDefaults();

        await Assert.ThrowsAsync<InvalidOperationException>(() => JobCompletion.FinalizeAsync(
            job, result, _ => throw new InvalidOperationException("database unavailable"), CancellationToken.None));

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(ProcessingStage.Finalizing, job.ProcessingStage);
        Assert.False(job.ReportReady);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public void ApiAndPdfUseTheSamePersistedFinalOutcome()
    {
        var job = JobWithDefaults();
        job.Status = JobStatus.Completed;
        job.AnalysisOutcome = JobAnalysisOutcome.CompletedWithAnalysisErrors;
        job.ReportReady = true;
        var response = JobResponseDto.FromJob(job);
        var pdfText = Encoding.Latin1.GetString(JobReportPdfGenerator.Generate(job));

        Assert.Equal("Completed with analysis errors", response.ResultSummary.Label);
        Assert.Contains(response.ResultSummary.Label, pdfText);
    }

    private static Job JobWithDefaults() => new()
    {
        RepoUrl = "https://github.com/example/repository",
        TestSummary = JobTestSummary.NotRun(TestSkipReason.BothPrerequisitesMissing)
    };

    private static RepositoryProcessingResult ResultWithDefaults() => new(
        JobTestSummary.NotRun(),
        JobCoverageAnalysis.NotRun("tests skipped"),
        new DotNetArchitectureAnalysis { Outcome = ArchitectureAnalysisOutcome.NotApplicable },
        BuildDiagnosticsRunner.NotApplicable(),
        new CodeComplexityAnalysis
        {
            CSharp = new LanguageComplexitySummary { Outcome = AnalyzerOutcome.NoSource },
            JavaScriptTypeScript = new LanguageComplexitySummary { Outcome = AnalyzerOutcome.NoSource }
        },
        new JavaScriptDependencyAnalysis { Outcome = AnalyzerOutcome.NoSource },
        null,
        "0123456789012345678901234567890123456789",
        "test-worker");
}

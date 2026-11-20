namespace CodeForge.Infrastructure.Models;

public sealed class JobTestSummary
{
    public TestRunOutcome Outcome { get; set; }

    public int? Executed { get; set; }

    public int? Passed { get; set; }

    public int? Failed { get; set; }

    public int? Skipped { get; set; }

    public bool IsPartial { get; set; }

    public TestSkipReason SkipReason { get; set; }

    public static JobTestSummary NotRun(TestSkipReason reason = TestSkipReason.None)
    {
        return new JobTestSummary
        {
            Outcome = TestRunOutcome.NotRun,
            SkipReason = reason
        };
    }
}

public enum TestSkipReason
{
    None,
    TrustedRepositoryOptInMissing,
    WorkerFeatureFlagDisabled,
    BothPrerequisitesMissing,
    BuildFailed,
    ProjectDiscoveryFailed
}

public enum TestRunOutcome
{
    Completed,
    NoTestProjects,
    ZeroTests,
    NotRun,
    UnsupportedRunner,
    ExecutionError,
    TimedOut,
    NotApplicable
}

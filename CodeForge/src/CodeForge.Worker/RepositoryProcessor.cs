using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection;
using CodeForge.Infrastructure.Models;

namespace CodeForge.Worker;

public sealed class RepositoryProcessor
{
    private const string BuildConfiguration = "Release";
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    private readonly ProcessRunner _processRunner;
    private readonly StaticAnalysisRunner _staticAnalysisRunner;
    private readonly BuildDiagnosticsRunner _buildDiagnosticsRunner;
    private readonly DotNetArchitectureRunner _architectureRunner;
    private readonly bool _testExecutionEnabled;

    public RepositoryProcessor(
        ProcessRunner processRunner,
        StaticAnalysisRunner staticAnalysisRunner,
        BuildDiagnosticsRunner buildDiagnosticsRunner,
        DotNetArchitectureRunner architectureRunner,
        IConfiguration configuration)
    {
        _processRunner = processRunner;
        _staticAnalysisRunner = staticAnalysisRunner;
        _buildDiagnosticsRunner = buildDiagnosticsRunner;
        _architectureRunner = architectureRunner;
        _testExecutionEnabled = bool.TryParse(
            configuration["DOTNET_TEST_EXECUTION_ENABLED"],
            out var enabled) && enabled;
    }

    public async Task<RepositoryProcessingResult> ProcessRepoAsync(
        Guid jobId,
        string repoUrl,
        bool runTests,
        Func<RepositoryProcessingResult, CancellationToken, Task> persistResult,
        Func<ProcessingStage, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "codeforge",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var repoPath = Path.Combine(workspace, "repo");

        try
        {
            await reportProgress(ProcessingStage.Cloning, cancellationToken);
            await CloneRepositoryAsync(repoUrl, repoPath, workspace, cancellationToken);
            var commitSha = await GetCommitShaAsync(repoPath, cancellationToken);

            await reportProgress(ProcessingStage.StaticAnalysis, cancellationToken);
            var staticAnalysis = await _staticAnalysisRunner.AnalyzeAsync(
                jobId,
                repoPath,
                workspace,
                cancellationToken);
            JobTestSummary testSummary;
            JobCoverageAnalysis coverageAnalysis;
            var buildFindings = BuildDiagnosticsRunner.Unavailable();
            var architecture = new DotNetArchitectureAnalysis { Outcome = ArchitectureAnalysisOutcome.NotApplicable };
            string? buildFailure = null;

            try
            {
                await reportProgress(ProcessingStage.ProjectDiscovery, cancellationToken);
                var repository = DiscoverRepository(repoPath);
                if (repository is null)
                {
                    testSummary = new JobTestSummary
                    {
                        Outcome = TestRunOutcome.NotApplicable
                    };
                    coverageAnalysis = new JobCoverageAnalysis { Outcome = CoverageOutcome.NotApplicable };
                    buildFindings = BuildDiagnosticsRunner.NotApplicable();
                }
                else
                {
                    try
                    {
                        architecture = await _architectureRunner.AnalyzeAsync(
                            jobId, repository.ProjectFiles, repoPath, workspace, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        architecture = new DotNetArchitectureAnalysis
                        {
                            Outcome = ArchitectureAnalysisOutcome.ToolError,
                            Limitations = [".NET architecture analysis failed independently of the repository build."]
                        };
                    }
                    await reportProgress(ProcessingStage.Building, cancellationToken);
                    var build = await _buildDiagnosticsRunner.BuildAsync(
                        repository.SolutionFile,
                        repository.ProjectFiles,
                        repoPath,
                        workspace,
                        cancellationToken);
                    buildFindings = build.Analysis;
                    buildFailure = build.FailureReason;
                    if (buildFailure is not null || !runTests || !_testExecutionEnabled)
                    {
                        var skipReason = buildFailure is not null
                            ? TestSkipReason.BuildFailed
                            : !runTests && !_testExecutionEnabled
                                ? TestSkipReason.BothPrerequisitesMissing
                                : !runTests
                                    ? TestSkipReason.TrustedRepositoryOptInMissing
                                    : TestSkipReason.WorkerFeatureFlagDisabled;
                        testSummary = JobTestSummary.NotRun(skipReason);
                        coverageAnalysis = JobCoverageAnalysis.NotRun(CoverageSkipMessage(skipReason));
                    }
                    else
                    {
                        await reportProgress(ProcessingStage.Testing, cancellationToken);
                        var execution = await RunTestsAsync(
                            repository,
                            workspace,
                            repoPath,
                            staticAnalysis.Complexity,
                            cancellationToken);
                        testSummary = execution.Tests;
                        coverageAnalysis = execution.Coverage;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RepositoryProcessingException exception)
            {
                testSummary = JobTestSummary.NotRun(TestSkipReason.ProjectDiscoveryFailed);
                coverageAnalysis = JobCoverageAnalysis.NotRun("Coverage was not collected because repository discovery failed.");
                buildFailure = exception.Message;
            }
            catch (Exception)
            {
                testSummary = JobTestSummary.NotRun(TestSkipReason.ProjectDiscoveryFailed);
                coverageAnalysis = JobCoverageAnalysis.NotRun("Coverage was not collected because build or test discovery failed.");
                buildFailure = "The .NET build or test discovery failed unexpectedly. Raw tool output is not exposed because repository contents are untrusted.";
            }

            var result = new RepositoryProcessingResult(
                testSummary,
                coverageAnalysis,
                architecture,
                buildFindings,
                staticAnalysis.Complexity,
                staticAnalysis.Dependencies,
                buildFailure,
                commitSha,
                GetWorkerVersion());
            await reportProgress(ProcessingStage.Finalizing, cancellationToken);
            await persistResult(result, cancellationToken);
            return result;
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static string CoverageSkipMessage(TestSkipReason reason) => reason switch
    {
        TestSkipReason.BuildFailed => "Coverage was not collected because tests were skipped after the repository build failed.",
        TestSkipReason.TrustedRepositoryOptInMissing => "Coverage was not collected because tests were skipped: trusted-repository opt-in is missing.",
        TestSkipReason.WorkerFeatureFlagDisabled => "Coverage was not collected because tests were skipped: the worker test-execution feature flag is disabled.",
        TestSkipReason.BothPrerequisitesMissing => "Coverage was not collected because tests were skipped: trusted-repository opt-in and the worker feature flag are both missing.",
        _ => "Coverage was not collected because tests could not be scheduled."
    };

    private async Task<string?> GetCommitShaAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync("git", ["rev-parse", "HEAD"], repoPath, TimeSpan.FromSeconds(10), cancellationToken);
            var value = result.StandardOutput?.Trim();
            return result.ExitCode == 0 && value?.Length == 40 ? value : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string GetWorkerVersion() =>
        typeof(RepositoryProcessor).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RepositoryProcessor).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private async Task CloneRepositoryAsync(
        string repoUrl,
        string repoPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "git",
            ["clone", "--depth", "1", repoUrl, repoPath],
            workspace,
            CloneTimeout,
            cancellationToken);

        if (result.TimeOut)
        {
            throw new RepositoryProcessingException(
                "Repository clone timed out after 20 seconds. The repository may be too large or temporarily unavailable.");
        }

        if (result.ExitCode != 0)
        {
            throw new RepositoryProcessingException(DescribeCloneFailure(result.StandardError));
        }
    }

    private async Task<TestExecutionResult> RunTestsAsync(
        RepositoryDiscovery repository,
        string workspace,
        string repoPath,
        CodeComplexityAnalysis complexity,
        CancellationToken cancellationToken)
    {
        var projects = repository.ProjectFiles
            .Select(project => ReadProject(project, repoPath))
            .ToArray();
        var testProjects = projects.Where(project => project.IsTestProject).ToArray();

        if (testProjects.Length == 0)
        {
            return new TestExecutionResult(
                new JobTestSummary { Outcome = TestRunOutcome.NoTestProjects },
                new JobCoverageAnalysis { Outcome = CoverageOutcome.NoTestProjects });
        }

        var supportedProjects = testProjects.Where(project => project.IsVstestCompatible).ToArray();
        if (supportedProjects.Length == 0)
        {
            return new TestExecutionResult(
                new JobTestSummary { Outcome = TestRunOutcome.UnsupportedRunner },
                new JobCoverageAnalysis
                {
                    Outcome = CoverageOutcome.UnsupportedCollector,
                    Limitations = ["Coverage collection currently supports VSTest projects using coverlet.collector."]
                });
        }

        var totals = new TrxTestCounts();
        var parsedFiles = new HashSet<string>(StringComparer.Ordinal);
        var sawReport = false;
        var sawExecutionError = false;
        var resultsRoot = Path.Combine(workspace, "test-results");
        var coverageReports = new HashSet<string>(StringComparer.Ordinal);
        var coverageParseFailed = false;
        var coverageProjects = supportedProjects.Count(project => project.SupportsCoverage);
        var runSettingsPath = Path.Combine(workspace, "codeforge-coverage.runsettings");
        await WriteCoverageRunSettingsAsync(runSettingsPath, cancellationToken);

        for (var projectIndex = 0; projectIndex < supportedProjects.Length; projectIndex++)
        {
            var project = supportedProjects[projectIndex];
            var frameworks = project.TargetFrameworks.Count == 0
                ? new string?[] { null }
                : project.TargetFrameworks.Cast<string?>().ToArray();

            for (var frameworkIndex = 0; frameworkIndex < frameworks.Length; frameworkIndex++)
            {
                var framework = frameworks[frameworkIndex];
                var runName = $"{projectIndex:D3}-{SafeName(Path.GetFileNameWithoutExtension(project.Path))}-{frameworkIndex:D2}-{SafeName(framework ?? "all")}";
                var resultsDirectory = Path.Combine(resultsRoot, runName);
                Directory.CreateDirectory(resultsDirectory);

                var arguments = new List<string>
                {
                    "test",
                    project.Path,
                    "--configuration",
                    BuildConfiguration,
                    "--no-build",
                    "--no-restore",
                    "--results-directory",
                    resultsDirectory,
                    "--logger",
                    $"trx;LogFileName={runName}.trx",
                    "--nologo"
                };

                if (project.SupportsCoverage)
                {
                    arguments.Add("--collect");
                    arguments.Add("XPlat Code Coverage");
                    arguments.Add("--settings");
                    arguments.Add(runSettingsPath);
                }

                if (framework is not null)
                {
                    arguments.Add("--framework");
                    arguments.Add(framework);
                }

                var result = await _processRunner.RunAsync(
                    "dotnet",
                    arguments,
                    repoPath,
                    TestTimeout,
                    cancellationToken);

                var reports = Directory
                    .EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .Where(parsedFiles.Add)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();

                var invocationCounts = new TrxTestCounts();
                foreach (var report in reports)
                {
                    try
                    {
                        invocationCounts.Add(TrxTestResultParser.Parse(report));
                        sawReport = true;
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
                    {
                        sawExecutionError = true;
                    }
                }

                totals.Add(invocationCounts);

                foreach (var coverageReport in Directory.EnumerateFiles(
                             resultsDirectory,
                             "coverage.cobertura.xml",
                             SearchOption.AllDirectories))
                {
                    coverageReports.Add(Path.GetFullPath(coverageReport));
                }

                if (result.TimeOut)
                {
                    var timedOut = CreateSummary(TestRunOutcome.TimedOut, totals, sawReport);
                    return CreateTestExecutionResult(timedOut, coverageReports, repoPath,
                        complexity, coverageProjects, supportedProjects.Length, coverageParseFailed);
                }

                if (reports.Length == 0 ||
                    invocationCounts.HasRunnerError ||
                    (result.ExitCode != 0 && invocationCounts.Failed == 0))
                {
                    sawExecutionError = true;
                }
            }
        }

        if (sawExecutionError || !sawReport)
        {
            var executionError = CreateSummary(TestRunOutcome.ExecutionError, totals, sawReport);
            return CreateTestExecutionResult(executionError, coverageReports, repoPath,
                complexity, coverageProjects, supportedProjects.Length, coverageParseFailed);
        }

        if (supportedProjects.Length != testProjects.Length)
        {
            var unsupported = CreateSummary(TestRunOutcome.UnsupportedRunner, totals, isPartial: true);
            return CreateTestExecutionResult(unsupported, coverageReports, repoPath,
                complexity, coverageProjects, supportedProjects.Length, coverageParseFailed);
        }

        var summary = CreateSummary(
            totals.Total == 0 ? TestRunOutcome.ZeroTests : TestRunOutcome.Completed,
            totals,
            isPartial: false);
        return CreateTestExecutionResult(summary, coverageReports, repoPath,
            complexity, coverageProjects, supportedProjects.Length, coverageParseFailed);
    }

    private static TestExecutionResult CreateTestExecutionResult(
        JobTestSummary tests,
        IReadOnlyCollection<string> reportPaths,
        string repoPath,
        CodeComplexityAnalysis complexity,
        int coverageProjects,
        int supportedProjects,
        bool parseFailed)
    {
        if (coverageProjects == 0)
        {
            return new TestExecutionResult(tests, new JobCoverageAnalysis
            {
                Outcome = CoverageOutcome.UnsupportedCollector,
                Limitations = ["No VSTest test project references coverlet.collector, so XPlat Code Coverage is unavailable."],
                CheckoutModified = false
            });
        }

        IReadOnlyList<FileCoverage> files;
        try
        {
            files = CoberturaCoverageParser.ParseAndCombine(reportPaths, repoPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or System.Xml.XmlException)
        {
            files = [];
            parseFailed = true;
        }

        if (reportPaths.Count == 0 || files.Count == 0 || files.All(file => file.MeasurableLines == 0))
        {
            return new TestExecutionResult(tests, new JobCoverageAnalysis
            {
                Outcome = CoverageOutcome.Unavailable,
                Limitations = ["The supported coverage collector did not produce a readable Cobertura report."],
                CheckoutModified = false
            });
        }

        var partial = parseFailed || coverageProjects != supportedProjects ||
                      tests.Outcome is TestRunOutcome.TimedOut or TestRunOutcome.ExecutionError or TestRunOutcome.UnsupportedRunner;
        var limitations = new List<string>
        {
            "Coverage measures application code exercised by tests; it does not prove correctness.",
            "Test assemblies and generated C# files are excluded where coverlet supports the configured filters.",
            "Overlapping target-framework and project reports are merged by repository-relative file and source line; a line is covered if any report covered it. Branch counts use the highest covered/measurable count per source line to avoid double counting.",
            "Function review priorities are limited to Lizard mappings with an explicit source length and the retained uncovered-line evidence."
        };
        if (coverageProjects != supportedProjects)
        {
            limitations.Add("Some VSTest projects do not reference coverlet.collector and have no coverage data.");
        }
        if (partial)
        {
            limitations.Add("Coverage is partial because one or more test or coverage invocations did not complete cleanly.");
        }

        var coveredLines = files.Sum(file => file.CoveredLines);
        var measurableLines = files.Sum(file => file.MeasurableLines);
        var branchFiles = files.Where(file => file.MeasurableBranches is not null).ToArray();
        var priorities = CoberturaCoverageParser.Correlate(files, complexity.CSharp);
        return new TestExecutionResult(tests, new JobCoverageAnalysis
        {
            Outcome = partial ? CoverageOutcome.Partial : CoverageOutcome.Completed,
            CoveredLines = coveredLines,
            MeasurableLines = measurableLines,
            CoveredBranches = branchFiles.Length == 0 ? null : branchFiles.Sum(file => file.CoveredBranches ?? 0),
            MeasurableBranches = branchFiles.Length == 0 ? null : branchFiles.Sum(file => file.MeasurableBranches ?? 0),
            Files = files,
            ReviewPriorities = priorities,
            Limitations = limitations,
            CheckoutModified = false
        });
    }

    private static Task WriteCoverageRunSettingsAsync(string path, CancellationToken cancellationToken)
    {
        const string settings = """
            <?xml version="1.0" encoding="utf-8"?>
            <RunSettings>
              <DataCollectionRunSettings>
                <DataCollectors>
                  <DataCollector friendlyName="XPlat Code Coverage">
                    <Configuration>
                      <Format>cobertura</Format>
                      <IncludeTestAssembly>false</IncludeTestAssembly>
                      <Exclude>[*.Tests]*,[*.Test]*</Exclude>
                      <ExcludeByFile>**/*.g.cs,**/*.g.i.cs,**/*.designer.cs,**/obj/**,**/bin/**</ExcludeByFile>
                    </Configuration>
                  </DataCollector>
                </DataCollectors>
              </DataCollectionRunSettings>
            </RunSettings>
            """;
        return File.WriteAllTextAsync(path, settings, cancellationToken);
    }

    private static JobTestSummary CreateSummary(
        TestRunOutcome outcome,
        TrxTestCounts counts,
        bool isPartial)
    {
        var hasCounts = counts.ReportCount > 0;
        return new JobTestSummary
        {
            Outcome = outcome,
            Executed = hasCounts ? counts.Executed : null,
            Passed = hasCounts ? counts.Passed : null,
            Failed = hasCounts ? counts.Failed : null,
            Skipped = hasCounts ? counts.Skipped : null,
            IsPartial = isPartial
        };
    }

    private static RepositoryDiscovery? DiscoverRepository(string repoPath)
    {
        var solutions = EnumerateByExtension(repoPath, ".sln", ".slnx");
        if (solutions.Length > 1)
        {
            var names = string.Join(
                ", ",
                solutions.Select(path => Path.GetRelativePath(repoPath, path)));
            throw new RepositoryProcessingException(
                $"Multiple solution files were found ({names}). CodeForge cannot choose one deterministically; keep one solution or provide an explicit solution selection.");
        }

        var solution = solutions.SingleOrDefault();
        var projects = solution is null
            ? EnumerateByExtension(repoPath, ".csproj")
            : ReadSolutionProjects(solution, repoPath);

        if (projects.Length == 0 && solution is null)
        {
            return null;
        }

        if (projects.Length == 0)
        {
            throw new RepositoryProcessingException(
                solution is null
                    ? "No supported .NET project was found. The repository must contain at least one .csproj file."
                    : "The selected solution does not contain any .csproj projects.");
        }

        return new RepositoryDiscovery(solution, projects);
    }

    private static string[] ReadSolutionProjects(string solutionFile, string repoPath)
    {
        IEnumerable<string?> projectPaths;
        if (Path.GetExtension(solutionFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = XDocument.Load(solutionFile, LoadOptions.None);
            projectPaths = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => element.Attribute("Path")?.Value);
        }
        else
        {
            var projectPattern = new Regex(
                "^Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.csproj)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            projectPaths = File.ReadLines(solutionFile)
                .Select(line => projectPattern.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["path"].Value);
        }

        var solutionDirectory = Path.GetDirectoryName(solutionFile)
            ?? throw new InvalidDataException("The solution path has no parent directory.");
        var normalizedRepoPath = Path.GetFullPath(repoPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return projectPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', Path.DirectorySeparatorChar))
            .Select(path => Path.GetFullPath(Path.Combine(solutionDirectory, path)))
            .Where(path => path.StartsWith(normalizedRepoPath, StringComparison.Ordinal))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => Path.GetRelativePath(repoPath, path), StringComparer.Ordinal)
            .ToArray();
    }

    private static ProjectDiscovery ReadProject(string projectFile, string repoPath)
    {
        var documents = EnumerateProjectConfigurationFiles(projectFile, repoPath)
            .Select(path => XDocument.Load(path, LoadOptions.None))
            .ToArray();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in documents)
        {
            foreach (var property in document
                .Descendants()
                .Where(element => element.Parent?.Name.LocalName == "PropertyGroup"))
            {
                properties[property.Name.LocalName] = property.Value.Trim();
            }

            foreach (var package in document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference"))
            {
                var name = package.Attribute("Include")?.Value
                    ?? package.Attribute("Update")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    packages.Add(name);
                }
            }
        }

        var explicitlyTestProject = properties.TryGetValue("IsTestProject", out var value) &&
            bool.TryParse(value, out var isTestProject) && isTestProject;
        var hasVstestSdk = packages.Contains("Microsoft.NET.Test.Sdk");
        var supportsCoverage = packages.Contains("coverlet.collector");
        var hasKnownTestFramework = packages.Any(IsKnownTestFramework);
        var targetFrameworkValue = properties.GetValueOrDefault("TargetFrameworks")
            ?? properties.GetValueOrDefault("TargetFramework")
            ?? string.Empty;
        var targetFrameworks = targetFrameworkValue
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(framework => !framework.Contains("$(", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToArray();

        return new ProjectDiscovery(
            projectFile,
            explicitlyTestProject || hasVstestSdk || hasKnownTestFramework,
            hasVstestSdk,
            supportsCoverage,
            targetFrameworks);
    }

    private static IEnumerable<string> EnumerateProjectConfigurationFiles(
        string projectFile,
        string repoPath)
    {
        var directories = new Stack<string>();
        var current = new DirectoryInfo(Path.GetDirectoryName(projectFile)!);
        var root = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);

        while (current is not null &&
               (current.FullName.Equals(root, StringComparison.Ordinal) ||
                current.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            directories.Push(current.FullName);
            current = current.Parent;
        }

        foreach (var directory in directories)
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }
        }

        yield return projectFile;
    }

    private static bool IsKnownTestFramework(string packageName)
    {
        return packageName.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
               packageName.Equals("NUnit", StringComparison.OrdinalIgnoreCase) ||
               packageName.Equals("MSTest.TestFramework", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] EnumerateByExtension(string root, params string[] extensions)
    {
        var extensionSet = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => extensionSet.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
    }

    private static string SafeName(string value)
    {
        var characters = value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-');
        return new string(characters.ToArray());
    }

    private static string DescribeCloneFailure(string? standardError)
    {
        if (standardError?.Contains("Repository not found", StringComparison.OrdinalIgnoreCase) == true ||
            standardError?.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Repository clone failed because the repository does not exist, is private, or the URL is incorrect.";
        }

        if (standardError?.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Repository clone failed because GitHub could not be reached. Check the worker's DNS and network connection.";
        }

        return "Repository clone failed. Verify that the URL is correct, the repository exists, and it is public.";
    }

    private sealed record RepositoryDiscovery(
        string? SolutionFile,
        IReadOnlyList<string> ProjectFiles);

    private sealed record ProjectDiscovery(
        string Path,
        bool IsTestProject,
        bool IsVstestCompatible,
        bool SupportsCoverage,
        IReadOnlyList<string> TargetFrameworks);
}

internal static class TrxTestResultParser
{
    public static TrxTestCounts Parse(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var counters = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Counters")
            ?? throw new InvalidDataException("The TRX report does not contain result counters.");
        var skippedResults = document
            .Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Count(element =>
            {
                var outcome = element.Attribute("outcome")?.Value;
                return outcome?.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) == true ||
                       outcome?.Equals("Skipped", StringComparison.OrdinalIgnoreCase) == true;
            });

        return new TrxTestCounts
        {
            Total = ReadCount(counters, "total"),
            Executed = ReadCount(counters, "executed"),
            Passed = ReadCount(counters, "passed"),
            Failed = ReadCount(counters, "failed"),
            Skipped = skippedResults > 0
                ? skippedResults
                : ReadCount(counters, "notExecuted"),
            HasRunnerError = ReadOptionalCount(counters, "error") > 0 ||
                             ReadOptionalCount(counters, "timeout") > 0 ||
                             ReadOptionalCount(counters, "aborted") > 0 ||
                             ReadOptionalCount(counters, "notRunnable") > 0,
            ReportCount = 1
        };
    }

    private static int ReadCount(XElement counters, string name)
    {
        var value = counters.Attribute(name)?.Value;
        return int.TryParse(value, out var count) && count >= 0
            ? count
            : throw new InvalidDataException($"The TRX counter '{name}' is missing or invalid.");
    }

    private static int ReadOptionalCount(XElement counters, string name)
    {
        var value = counters.Attribute(name)?.Value;
        return int.TryParse(value, out var count) && count >= 0 ? count : 0;
    }
}

internal sealed class TrxTestCounts
{
    public int Total { get; set; }
    public int Executed { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int ReportCount { get; set; }
    public bool HasRunnerError { get; set; }

    public void Add(TrxTestCounts other)
    {
        Total += other.Total;
        Executed += other.Executed;
        Passed += other.Passed;
        Failed += other.Failed;
        Skipped += other.Skipped;
        ReportCount += other.ReportCount;
        HasRunnerError |= other.HasRunnerError;
    }
}

public sealed class RepositoryProcessingException : Exception
{
    public RepositoryProcessingException(string message) : base(message)
    {
    }
}

public sealed record RepositoryProcessingResult(
    JobTestSummary Tests,
    JobCoverageAnalysis Coverage,
    DotNetArchitectureAnalysis Architecture,
    BuildFindingsAnalysis BuildFindings,
    CodeComplexityAnalysis Complexity,
    JavaScriptDependencyAnalysis Dependencies,
    string? BuildFailure,
    string? RepositoryCommitSha,
    string WorkerVersion);

internal sealed record TestExecutionResult(JobTestSummary Tests, JobCoverageAnalysis Coverage);

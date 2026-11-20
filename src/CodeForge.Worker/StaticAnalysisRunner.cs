using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeForge.Infrastructure.Models;
using CodeForge.Infrastructure.Reports;
using Microsoft.Extensions.Logging;

namespace CodeForge.Worker;

public sealed class StaticAnalysisRunner
{
    private const int MaximumLizardFilesPerLanguage = 5000;
    private const int MaximumMadgeFiles = 2000;
    private const string MadgeExcludePattern =
        "(^|/)(node_modules|bin|obj|dist|build|coverage|\\.next|out|packages|vendor)(/|$)";
    private static readonly TimeSpan LizardTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MadgeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GraphvizTimeout = TimeSpan.FromSeconds(45);
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git", ".svn", ".hg", ".next", ".nuxt", ".cache",
            "node_modules", "bin", "obj", "dist", "build", "coverage",
            "out", "packages", "vendor"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CSharpExtensions = new(
        [".cs"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> JavaScriptExtensions = new(
        [".js", ".jsx", ".ts", ".tsx"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LizardFunctionPattern = new(
        "^(?<name>.*) at (?<path>.*):(?<line>[0-9]+)$",
        RegexOptions.CultureInvariant);

    private readonly ProcessRunner _processRunner;
    private readonly IAnalysisArtifactStore _artifactStore;
    private readonly ILogger<StaticAnalysisRunner> _logger;

    public StaticAnalysisRunner(
        ProcessRunner processRunner,
        IAnalysisArtifactStore artifactStore,
        ILogger<StaticAnalysisRunner> logger)
    {
        _processRunner = processRunner;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task<StaticAnalysisResult> AnalyzeAsync(
        Guid jobId,
        string repoPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        var csharpFiles = EnumerateSourceFiles(repoPath, CSharpExtensions).ToArray();
        var javascriptFiles = EnumerateSourceFiles(repoPath, JavaScriptExtensions).ToArray();
        var lizardVersion = await GetToolVersionAsync("lizard", ["--version"], repoPath, cancellationToken);
        var madgeVersion = await GetToolVersionAsync("madge", ["--version"], repoPath, cancellationToken);

        var csharpTask = RunLizardAsync(
            repoPath,
            workspace,
            "csharp",
            ["csharp"],
            csharpFiles,
            lizardVersion,
            cancellationToken);
        var javascriptTask = RunLizardAsync(
            repoPath,
            workspace,
            "javascript-typescript",
            ["javascript", "typescript", "jsx", "tsx"],
            javascriptFiles,
            lizardVersion,
            cancellationToken);

        await Task.WhenAll(csharpTask, javascriptTask);
        var dependencyAnalysis = await RunMadgeAsync(
            jobId,
            repoPath,
            workspace,
            javascriptFiles,
            madgeVersion,
            cancellationToken);

        return new StaticAnalysisResult(
            new CodeComplexityAnalysis
            {
                CSharp = await csharpTask,
                JavaScriptTypeScript = await javascriptTask
            },
            dependencyAnalysis);
    }

    private async Task<LanguageComplexitySummary> RunLizardAsync(
        string repoPath,
        string workspace,
        string name,
        IReadOnlyList<string> languages,
        IReadOnlyList<string> discoveredFiles,
        string? toolVersion,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (discoveredFiles.Count == 0)
        {
            return new LanguageComplexitySummary
            {
                Outcome = AnalyzerOutcome.NoSource,
                FunctionsAnalyzed = 0,
                FunctionsOverThreshold = 0,
                Execution = Details("lizard", toolVersion, stopwatch, null, null)
            };
        }

        var wasTruncated = discoveredFiles.Count > MaximumLizardFilesPerLanguage;
        var files = discoveredFiles.Take(MaximumLizardFilesPerLanguage).ToArray();
        var inputPath = Path.Combine(workspace, $"lizard-{name}-input.txt");
        var outputPath = Path.Combine(workspace, $"lizard-{name}.xml");

        try
        {
            await File.WriteAllLinesAsync(inputPath, files, cancellationToken);
            var arguments = new List<string>();
            foreach (var language in languages)
            {
                arguments.Add("--languages");
                arguments.Add(language);
            }

            arguments.AddRange(
            [
                "--CCN", CodeComplexityAnalysis.ComplexityThreshold.ToString(CultureInfo.InvariantCulture),
                "--ignore_warnings", "-1",
                "--xml",
                "--output_file", outputPath,
                "--input_file", inputPath
            ]);

            var result = await _processRunner.RunAsync(
                "lizard",
                arguments,
                repoPath,
                LizardTimeout,
                cancellationToken);

            if (result.TimeOut)
            {
                return new LanguageComplexitySummary
                {
                    Outcome = AnalyzerOutcome.TimedOut,
                    Execution = Details("lizard", toolVersion, stopwatch, result.ExitCode, "The complexity analyzer exceeded its 2 minute timeout.")
                };
            }

            if (result.ExitCode != 0)
            {
                return new LanguageComplexitySummary
                {
                    Outcome = AnalyzerOutcome.ExecutionFailed,
                    Execution = Details("lizard", toolVersion, stopwatch, result.ExitCode,
                        SanitizeError(result.StandardError, repoPath, "Lizard exited without producing a report."))
                };
            }

            if (!File.Exists(outputPath))
                return new LanguageComplexitySummary
                {
                    Outcome = AnalyzerOutcome.MalformedOutput,
                    Execution = Details("lizard", toolVersion, stopwatch, result.ExitCode, "Lizard exited successfully but did not create its XML report.")
                };

            IReadOnlyList<ComplexityFinding> findings;
            try { findings = ParseLizardReport(outputPath, repoPath); }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
            {
                return new LanguageComplexitySummary
                {
                    Outcome = AnalyzerOutcome.MalformedOutput,
                    Execution = Details("lizard", toolVersion, stopwatch, result.ExitCode, "Lizard produced XML that CodeForge could not parse safely.")
                };
            }
            return new LanguageComplexitySummary
            {
                Outcome = wasTruncated ? AnalyzerOutcome.Partial : AnalyzerOutcome.Completed,
                FunctionsAnalyzed = findings.Count,
                FunctionsOverThreshold = findings.Count(finding =>
                    finding.Score > CodeComplexityAnalysis.ComplexityThreshold),
                HighestComplexityFunctions = findings
                    .OrderByDescending(finding => finding.Score)
                    .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.Line)
                    .Take(5)
                    .ToArray(),
                FunctionsOverThresholdDetails = findings
                    .Where(finding => finding.Score > CodeComplexityAnalysis.ComplexityThreshold)
                    .OrderByDescending(finding => finding.Score)
                    .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                    .ThenBy(finding => finding.Line)
                    .Take(500)
                    .ToArray(),
                Execution = Details("lizard", toolVersion, stopwatch, result.ExitCode, null)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception)
        {
            return new LanguageComplexitySummary
            {
                Outcome = AnalyzerOutcome.ToolUnavailable,
                Execution = Details("lizard", toolVersion, stopwatch, null, "The lizard executable is not installed or is not available on the worker PATH.")
            };
        }
        catch (Exception)
        {
            return new LanguageComplexitySummary
            {
                Outcome = AnalyzerOutcome.ToolError,
                Execution = Details("lizard", toolVersion, stopwatch, null, "The complexity analyzer failed unexpectedly. Detailed diagnostics were written to worker logs.")
            };
        }
    }

    private async Task<JavaScriptDependencyAnalysis> RunMadgeAsync(
        Guid jobId,
        string repoPath,
        string workspace,
        IReadOnlyList<string> discoveredFiles,
        string? toolVersion,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (discoveredFiles.Count == 0)
        {
            return new JavaScriptDependencyAnalysis
            {
                Outcome = AnalyzerOutcome.NoSource,
                SourceFileCount = 0,
                ModuleCount = 0,
                DependencyCount = 0,
                CycleCount = 0,
                Execution = Details("madge", toolVersion, stopwatch, null, null)
            };
        }

        var wasTruncated = discoveredFiles.Count > MaximumMadgeFiles;
        var files = discoveredFiles
            .Take(MaximumMadgeFiles)
            .Select(Path.GetFullPath)
            .ToArray();
        var commonArguments = new List<string>
        {
            "--basedir", repoPath,
            "--extensions", "js,jsx,ts,tsx",
            "--exclude", MadgeExcludePattern,
            "--no-spinner",
            "--no-color"
        };

        try
        {
            var graphArguments = new List<string>(commonArguments)
            {
                "--warning",
                "--json"
            };
            graphArguments.AddRange(files);
            var graphResult = await _processRunner.RunAsync(
                "madge",
                graphArguments,
                workspace,
                MadgeTimeout,
                cancellationToken);

            if (graphResult.TimeOut)
            {
                return new JavaScriptDependencyAnalysis
                {
                    Outcome = AnalyzerOutcome.TimedOut,
                    SourceFileCount = discoveredFiles.Count,
                    Execution = Details("madge", toolVersion, stopwatch, graphResult.ExitCode, "Madge exceeded its 2 minute timeout.")
                };
            }

            if (graphResult.ExitCode != 0)
            {
                return new JavaScriptDependencyAnalysis
                {
                    Outcome = AnalyzerOutcome.ExecutionFailed,
                    SourceFileCount = discoveredFiles.Count,
                    Execution = Details("madge", toolVersion, stopwatch, graphResult.ExitCode,
                        SanitizeError(graphResult.StandardError, repoPath, "Madge exited without producing a dependency graph."))
                };
            }
            if (!TryParseDependencyGraph(graphResult.StandardOutput, out var graph))
                return new JavaScriptDependencyAnalysis
                {
                    Outcome = AnalyzerOutcome.MalformedOutput,
                    SourceFileCount = discoveredFiles.Count,
                    Execution = Details("madge", toolVersion, stopwatch, graphResult.ExitCode, "Madge produced output that was not a valid JSON dependency graph.")
                };

            var mayBeIncomplete = wasTruncated ||
                !string.IsNullOrWhiteSpace(graphResult.StandardError);
            var partial = false;
            IReadOnlyList<IReadOnlyList<string>> cycles = [];
            int? cycleCount = null;

            var cycleArguments = new List<string>(commonArguments)
            {
                "--circular",
                "--json"
            };
            cycleArguments.AddRange(files);
            var cycleResult = await _processRunner.RunAsync(
                "madge",
                cycleArguments,
                workspace,
                MadgeTimeout,
                cancellationToken);

            if (!cycleResult.TimeOut &&
                cycleResult.ExitCode is 0 or 1 &&
                TryParseCycles(cycleResult.StandardOutput, out var parsedCycles))
            {
                cycles = parsedCycles;
                cycleCount = parsedCycles.Count;
            }
            else
            {
                partial = true;
            }

            var graphAvailable = await TryCreateAndSaveGraphAsync(
                jobId,
                graph,
                workspace,
                repoPath,
                cancellationToken);
            if (!graphAvailable)
            {
                partial = true;
            }

            return new JavaScriptDependencyAnalysis
            {
                Outcome = partial || wasTruncated
                    ? AnalyzerOutcome.Partial
                    : AnalyzerOutcome.Completed,
                SourceFileCount = discoveredFiles.Count,
                ModuleCount = graph.Count,
                DependencyCount = graph.Values.Sum(dependencies => dependencies.Count),
                CycleCount = cycleCount,
                Cycles = cycles,
                MayBeIncomplete = mayBeIncomplete,
                GraphAvailable = graphAvailable,
                Execution = Details("madge", toolVersion, stopwatch, graphResult.ExitCode,
                    partial ? "Dependency analysis completed with incomplete cycle detection or graph rendering." : null)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception)
        {
            return new JavaScriptDependencyAnalysis
            {
                Outcome = AnalyzerOutcome.ToolUnavailable,
                SourceFileCount = discoveredFiles.Count,
                Execution = Details("madge", toolVersion, stopwatch, null, "The madge executable is not installed or is not available on the worker PATH.")
            };
        }
        catch (Exception)
        {
            return new JavaScriptDependencyAnalysis
            {
                Outcome = AnalyzerOutcome.ToolError,
                SourceFileCount = discoveredFiles.Count,
                Execution = Details("madge", toolVersion, stopwatch, null, "The dependency analyzer failed unexpectedly. Detailed diagnostics were written to worker logs.")
            };
        }
    }

    private async Task<string?> GetToolVersionAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(tool, arguments, workingDirectory, TimeSpan.FromSeconds(10), cancellationToken);
            return !result.TimeOut && result.ExitCode == 0
                ? (result.StandardOutput ?? result.StandardError)?.Trim()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static AnalyzerExecutionDetails Details(
        string tool,
        string? version,
        Stopwatch stopwatch,
        int? exitCode,
        string? reason) => new()
        {
            Tool = tool,
            Version = string.IsNullOrWhiteSpace(version) ? null : version,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            ExitCode = exitCode,
            ErrorReason = reason
        };

    private static string SanitizeError(string? error, string repoPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(error)) return fallback;
        var sanitized = error.Replace(repoPath, "<repository>", StringComparison.Ordinal)
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 500 ? sanitized : sanitized[..500] + "...";
    }

    private async Task<bool> TryCreateAndSaveGraphAsync(
        Guid jobId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        string workspace,
        string repoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var dotPath = Path.Combine(workspace, "dependency-graph.dot");
            var pngPath = Path.Combine(workspace, "dependency-graph.png");
            var jpegPath = Path.Combine(workspace, "dependency-graph.jpg");
            await File.WriteAllTextAsync(
                dotPath,
                BuildDotGraph(graph),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var pngResult = await _processRunner.RunAsync(
                "dot",
                ["-Tpng", dotPath, "-o", pngPath],
                repoPath,
                GraphvizTimeout,
                cancellationToken);
            var jpegResult = await _processRunner.RunAsync(
                "dot",
                ["-Tjpg", dotPath, "-o", jpegPath],
                repoPath,
                GraphvizTimeout,
                cancellationToken);

            if (pngResult.TimeOut || jpegResult.TimeOut ||
                pngResult.ExitCode != 0 || jpegResult.ExitCode != 0 ||
                !File.Exists(pngPath) || !File.Exists(jpegPath))
            {
                _logger.LogWarning(
                    "Dependency graph rendering failed. PNG exit {PngExitCode}, JPEG exit {JpegExitCode}, PNG timeout {PngTimeout}, JPEG timeout {JpegTimeout}, PNG error {PngError}, JPEG error {JpegError}",
                    pngResult.ExitCode,
                    jpegResult.ExitCode,
                    pngResult.TimeOut,
                    jpegResult.TimeOut,
                    pngResult.StandardError,
                    jpegResult.StandardError);
                return false;
            }

            var png = await File.ReadAllBytesAsync(pngPath, cancellationToken);
            var jpeg = await File.ReadAllBytesAsync(jpegPath, cancellationToken);
            await _artifactStore.SaveDependencyGraphAsync(
                jobId,
                png,
                jpeg,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Dependency graph rendering or artifact persistence failed");
            return false;
        }
    }

    private static IReadOnlyList<ComplexityFinding> ParseLizardReport(
        string path,
        string repoPath)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var functionMeasure = document
            .Descendants("measure")
            .FirstOrDefault(element =>
                element.Attribute("type")?.Value == "Function")
            ?? throw new InvalidDataException("The Lizard report has no function measure.");
        var labels = functionMeasure
            .Descendants("labels")
            .Elements("label")
            .Select(element => element.Value)
            .ToArray();
        var ccnIndex = Array.FindIndex(labels, label => label == "CCN");
        var lengthIndex = Array.FindIndex(labels, label =>
            label.Equals("length", StringComparison.OrdinalIgnoreCase));
        if (ccnIndex < 0)
        {
            throw new InvalidDataException("The Lizard report has no CCN value.");
        }

        var findings = new List<ComplexityFinding>();
        foreach (var item in functionMeasure.Elements("item"))
        {
            var match = LizardFunctionPattern.Match(item.Attribute("name")?.Value ?? string.Empty);
            var values = item.Elements("value").Select(element => element.Value).ToArray();
            if (!match.Success ||
                ccnIndex >= values.Length ||
                !int.TryParse(values[ccnIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) ||
                !int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
            {
                continue;
            }

            var filePath = Path.GetFullPath(match.Groups["path"].Value);
            var endLine = line;
            if (lengthIndex >= 0 && lengthIndex < values.Length &&
                int.TryParse(values[lengthIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) &&
                length > 0)
            {
                endLine = line + length - 1;
            }
            findings.Add(new ComplexityFinding
            {
                FunctionName = match.Groups["name"].Value,
                RelativePath = NormalizePath(Path.GetRelativePath(repoPath, filePath)),
                Line = line,
                EndLine = endLine,
                Score = score
            });
        }

        return findings;
    }

    private static bool TryParseDependencyGraph(
        string? json,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var module in document.RootElement.EnumerateObject())
            {
                result[NormalizePath(module.Name)] = module.Value
                    .EnumerateArray()
                    .Select(value => NormalizePath(value.GetString() ?? string.Empty))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
            }

            graph = result;
            return true;
        }
        catch (JsonException)
        {
            graph = new Dictionary<string, IReadOnlyList<string>>();
            return false;
        }
    }

    private static bool TryParseCycles(
        string? json,
        out IReadOnlyList<IReadOnlyList<string>> cycles)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            cycles = document.RootElement
                .EnumerateArray()
                .Select(cycle => (IReadOnlyList<string>)cycle
                    .EnumerateArray()
                    .Select(value => NormalizePath(value.GetString() ?? string.Empty))
                    .ToArray())
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            cycles = [];
            return false;
        }
    }

    private static string BuildDotGraph(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var builder = new StringBuilder();
        builder.AppendLine("digraph dependencies {");
        builder.AppendLine("  rankdir=LR;");
        builder.AppendLine("  graph [bgcolor=\"white\", pad=\"0.2\", nodesep=\"0.35\", ranksep=\"0.5\"];");
        builder.AppendLine("  node [shape=box, style=\"rounded,filled\", fillcolor=\"#f3f1e9\", color=\"#161714\", fontname=\"Helvetica\", fontsize=10];");
        builder.AppendLine("  edge [color=\"#77786f\"];");

        foreach (var module in graph.Keys)
        {
            builder.Append("  \"").Append(EscapeDot(module)).AppendLine("\";");
        }

        foreach (var (module, dependencies) in graph)
        {
            foreach (var dependency in dependencies)
            {
                builder.Append("  \"")
                    .Append(EscapeDot(module))
                    .Append("\" -> \"")
                    .Append(EscapeDot(dependency))
                    .AppendLine("\";");
            }
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string root,
        IReadOnlySet<string> extensions)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory
                .EnumerateDirectories(directory)
                .Where(path => !ExcludedDirectories.Contains(Path.GetFileName(path)))
                .Where(path => !IsSymbolicLink(path))
                .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                pending.Push(child);
            }

            foreach (var file in Directory
                .EnumerateFiles(directory)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Where(path => !IsSymbolicLink(path))
                .Where(path => !IsGeneratedSource(path))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                yield return file;
            }
        }
    }

    private static bool IsGeneratedSource(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSymbolicLink(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/');
    }

    private static string EscapeDot(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

public sealed record StaticAnalysisResult(
    CodeComplexityAnalysis Complexity,
    JavaScriptDependencyAnalysis Dependencies);

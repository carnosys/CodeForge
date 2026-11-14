using System.Globalization;
using System.Text;
using CodeForge.Infrastructure.Models;

namespace CodeForge.Api.Reports;

public static class JobReportPdfGenerator
{
    private const int MaximumLineLength = 88;
    private const int LinesPerPage = 43;

    public static byte[] Generate(
        Job job,
        ReadOnlyMemory<byte>? dependencyGraphJpeg = null,
        ReadOnlyMemory<byte>? dotNetProjectGraphJpeg = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var pages = BuildReportLines(job, dependencyGraphJpeg.HasValue, dotNetProjectGraphJpeg.HasValue)
            .Chunk(LinesPerPage)
            .Select(page => page.ToArray())
            .ToArray();

        return BuildPdf(pages, dependencyGraphJpeg, dotNetProjectGraphJpeg);
    }

    private static List<string> BuildReportLines(
        Job job,
        bool dependencyGraphIncluded,
        bool dotNetProjectGraphIncluded)
    {
        var lines = new List<string>();

        AddField(lines, "Job ID", job.Id.ToString());
        AddField(lines, "Repository", job.RepoUrl);
        AddField(lines, "Title", job.JobTitle ?? "Untitled build");
        AddField(lines, "Status", job.Status.ToString());
        AddField(lines, "Queued", FormatDate(job.CreatedAt));
        AddField(lines, "Started", FormatDate(job.StartedAt));
        AddField(lines, "Finished", FormatDate(job.CompletedAt));
        AddField(lines, "Repository commit", job.RepositoryCommitSha ?? "Unavailable");
        AddField(lines, "Worker version", job.WorkerVersion ?? "Unavailable");
        lines.Add(string.Empty);

        lines.Add("DESCRIPTION");
        lines.AddRange(Wrap(job.JobDescription ?? "No description was provided."));
        lines.Add(string.Empty);

        AddBuildFindings(lines, job.GetBuildFindings());
        lines.Add(string.Empty);

        AddDotNetArchitecture(lines, job.GetDotNetArchitecture(), dotNetProjectGraphIncluded);
        lines.Add(string.Empty);

        lines.Add("TEST RESULTS");
        lines.AddRange(Wrap(FormatTestResults(job.TestSummary)));
        lines.Add(string.Empty);

        AddCoverageResults(lines, job.GetCoverageAnalysis());
        lines.Add(string.Empty);

        AddComplexityResults(lines, job.GetComplexityAnalysis());
        lines.Add(string.Empty);

        AddDependencyResults(
            lines,
            job.GetDependencyAnalysis(),
            dependencyGraphIncluded);
        lines.Add(string.Empty);

        lines.Add("PROCESSING SUMMARY");
        var summary = JobResultSummary.FromJob(job);
        lines.AddRange(Wrap(summary.Label));
        lines.AddRange(Wrap(summary.Description));
        foreach (var failure in summary.FailedStages) lines.AddRange(Wrap($"  Failed/incomplete: {failure}"));
        foreach (var skipped in summary.SkippedStages) lines.AddRange(Wrap($"  {skipped}"));

        return lines;
    }

    private static void AddBuildFindings(
        List<string> lines,
        BuildFindingsAnalysis? analysis)
    {
        lines.Add("BUILD FINDINGS");
        if (analysis is null)
        {
            lines.Add("Build findings unavailable");
            return;
        }

        lines.AddRange(Wrap(
            $".NET SDK {analysis.SdkVersion ?? "unknown"} · {analysis.BuildConfiguration} · " +
            analysis.AnalysisProfile));
        lines.Add(analysis.AdditionalRulesEnabled
            ? "CodeForge additional analyzer rules were enabled."
            : "No CodeForge analyzer rules were added.");

        if (analysis.Outcome == BuildFindingsOutcome.NotApplicable)
        {
            lines.Add(".NET build findings not applicable");
            return;
        }

        if (analysis.Outcome == BuildFindingsOutcome.Unsupported)
        {
            lines.Add("Structured diagnostic collection unsupported");
            return;
        }

        if (analysis.Outcome == BuildFindingsOutcome.Unavailable)
        {
            lines.Add("Structured build findings unavailable");
            return;
        }

        var counts = analysis.Findings
            .GroupBy(finding => finding.ReportedSeverity, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => SeverityOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Count()} {group.Key}")
            .ToArray();
        if (analysis.Findings.Count == 0)
        {
            lines.Add(analysis.Outcome == BuildFindingsOutcome.Partial
                ? "No findings were recovered from the partial report"
                : "No compiler or analyzer findings");
            return;
        }

        lines.AddRange(Wrap(
            $"{analysis.Findings.Count} findings · {string.Join(" · ", counts)}" +
            (analysis.Outcome == BuildFindingsOutcome.Partial ? " · partial" : string.Empty)));
        foreach (var finding in analysis.Findings)
        {
            var location = finding.RelativePath is null
                ? "Location unavailable"
                : finding.RelativePath +
                  (finding.Line is null
                      ? string.Empty
                      : $":{finding.Line}" +
                        (finding.Column is null ? string.Empty : $":{finding.Column}"));
            lines.AddRange(Wrap(
                $"{finding.RuleId} [{finding.ReportedSeverity}] · {location}"));
            lines.AddRange(Wrap($"  {finding.Message}"));

            var guidance = BuildFindingGuidance.Get(finding.RuleId);
            if (guidance is not null)
            {
                lines.AddRange(Wrap($"  Explanation: {guidance.Explanation}"));
                lines.AddRange(Wrap($"  Suggested action: {guidance.SuggestedAction}"));
            }

            if (finding.HelpLink is not null)
            {
                lines.AddRange(Wrap($"  Help: {finding.HelpLink}"));
            }
        }
    }

    private static void AddDotNetArchitecture(
        List<string> lines,
        DotNetArchitectureAnalysis? analysis,
        bool graphIncluded)
    {
        lines.Add(".NET PROJECT DEPENDENCIES");
        if (analysis is null)
        {
            lines.Add(".NET project dependency results unavailable");
            return;
        }
        if (analysis.Outcome == ArchitectureAnalysisOutcome.NotApplicable)
        {
            lines.Add("No .NET projects found");
            return;
        }
        lines.AddRange(Wrap($"{analysis.Projects.Count} projects · {analysis.References.Count} direct references · discovery {analysis.DiscoveryMode}" +
                            (analysis.Outcome == ArchitectureAnalysisOutcome.Partial ? " · partial" : string.Empty)));
        foreach (var project in analysis.Projects)
        {
            var targets = analysis.References.Where(edge => edge.Source == project.Path).Select(edge => edge.Target).ToArray();
            lines.AddRange(Wrap(targets.Length == 0
                ? $"  {project.Path} -> (no project references)"
                : $"  {project.Path} -> {string.Join(", ", targets)}"));
        }
        if (!analysis.RulesConfigured)
        {
            lines.Add("Skipped: no architecture rules configuration supplied.");
        }
        else
        {
            lines.Add($"Architecture rules: {analysis.Rules.Count} valid · {analysis.Violations.Count} violations");
            foreach (var violation in analysis.Violations)
                lines.AddRange(Wrap($"  VIOLATION {violation.RuleId}: {violation.Source} -> {violation.Target}" +
                                    (string.IsNullOrWhiteSpace(violation.Description) ? string.Empty : $" · {violation.Description}")));
            foreach (var error in analysis.ConfigurationErrors)
                lines.AddRange(Wrap($"  Configuration error: {error}"));
        }
        foreach (var limitation in analysis.Limitations) lines.AddRange(Wrap($"  Limitation: {limitation}"));
        if (analysis.GraphTruncated) lines.Add("The image is a bounded overview; the textual dependency list above is complete.");
        lines.Add(graphIncluded ? ".NET project graph included on a following page." : ".NET project graph unavailable.");
    }

    private static int SeverityOrder(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "error" => 0,
            "warning" => 1,
            "note" or "info" => 2,
            "none" or "hidden" => 3,
            _ => 4
        };
    }

    private static void AddComplexityResults(
        List<string> lines,
        CodeComplexityAnalysis? analysis)
    {
        lines.Add("CODE COMPLEXITY");
        if (analysis is null)
        {
            lines.Add("Code complexity results unavailable");
            return;
        }

        AddLanguageComplexity(lines, "C#", analysis.CSharp);
        AddLanguageComplexity(
            lines,
            "JavaScript/TypeScript",
            analysis.JavaScriptTypeScript);
    }

    private static void AddCoverageResults(List<string> lines, JobCoverageAnalysis? coverage)
    {
        lines.Add("TEST COVERAGE");
        if (coverage is null)
        {
            lines.Add("Coverage unavailable for this older or unfinished analysis.");
            return;
        }

        if (coverage.Outcome is not (CoverageOutcome.Completed or CoverageOutcome.Partial) ||
            coverage.CoveredLines is null || coverage.MeasurableLines is null)
        {
            lines.Add(coverage.Outcome switch
            {
                CoverageOutcome.NotRun => "Coverage not collected",
                CoverageOutcome.NoTestProjects => "No test projects",
                CoverageOutcome.UnsupportedCollector => "Coverage collector unsupported",
                CoverageOutcome.NotApplicable => ".NET coverage not applicable",
                _ => "Coverage unavailable"
            });
            foreach (var limitation in coverage.Limitations)
            {
                lines.AddRange(Wrap($"  {limitation}"));
            }
            return;
        }

        var linePercent = Percentage(coverage.CoveredLines.Value, coverage.MeasurableLines.Value);
        var summary = $"Line coverage {linePercent} · {coverage.CoveredLines}/{coverage.MeasurableLines} measurable lines";
        if (coverage.CoveredBranches is not null && coverage.MeasurableBranches is not null)
        {
            summary += $" · Branch coverage {Percentage(coverage.CoveredBranches.Value, coverage.MeasurableBranches.Value)} ({coverage.CoveredBranches}/{coverage.MeasurableBranches})";
        }
        else
        {
            summary += " · Branch coverage unavailable";
        }
        if (coverage.Outcome == CoverageOutcome.Partial) summary += " · partial";
        lines.AddRange(Wrap(summary));
        lines.Add("Coverage measures application code exercised by tests; it does not prove correctness.");

        foreach (var file in coverage.Files
                     .Where(file => file.UncoveredLines.Count > 0)
                     .OrderBy(file => file.MeasurableLines == 0 ? 101m : file.CoveredLines * 100m / file.MeasurableLines)
                     .ThenBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var uncovered = string.Join(", ", file.UncoveredLines.Take(30));
            if (file.UncoveredLines.Count > 30) uncovered += ", ...";
            lines.AddRange(Wrap($"  {file.RelativePath} · {Percentage(file.CoveredLines, file.MeasurableLines)} · uncovered lines {uncovered}"));
        }

        if (coverage.ReviewPriorities.Count > 0)
        {
            lines.Add("COMPLEXITY AND COVERAGE REVIEW PRIORITIES");
            lines.Add("Functions listed here exceed the complexity threshold and contain uncovered executable lines within a supported source range.");
            foreach (var priority in coverage.ReviewPriorities)
            {
                lines.AddRange(Wrap(
                    $"  {priority.FunctionName} · complexity {priority.Complexity} · " +
                    $"{priority.RelativePath}:{priority.StartLine}-{priority.EndLine} · " +
                    $"uncovered lines {string.Join(", ", priority.UncoveredLines)}"));
            }
        }

        lines.Add(coverage.CheckoutModified
            ? "The submitted checkout was temporarily modified for collection."
            : "The submitted checkout was not modified; collection used a worker-owned temporary runsettings file.");
        foreach (var limitation in coverage.Limitations)
        {
            lines.AddRange(Wrap($"  Limitation: {limitation}"));
        }
    }

    private static string Percentage(int covered, int measurable) =>
        measurable <= 0
            ? "unavailable"
            : $"{Math.Round(covered * 100m / measurable, 1).ToString("0.0", CultureInfo.InvariantCulture)}%";

    private static void AddLanguageComplexity(
        List<string> lines,
        string label,
        LanguageComplexitySummary summary)
    {
        if (summary.Outcome == AnalyzerOutcome.NoSource)
        {
            lines.Add($"{label}: No applicable source found");
            return;
        }

        var status = summary.Outcome switch
        {
            AnalyzerOutcome.TimedOut => "Analysis timed out",
            AnalyzerOutcome.ToolError => "Analyzer error",
            AnalyzerOutcome.Partial => "Partial results",
            _ => "Completed"
        };

        if (summary.FunctionsAnalyzed is null ||
            summary.FunctionsOverThreshold is null)
        {
            lines.AddRange(Wrap($"{label}: {status}"));
            AddAnalyzerExecution(lines, summary.Execution);
            return;
        }

        lines.AddRange(Wrap(
            $"{label}: {summary.FunctionsAnalyzed} functions analyzed · " +
            $"{summary.FunctionsOverThreshold} over complexity threshold " +
            CodeComplexityAnalysis.ComplexityThreshold +
            (summary.Outcome == AnalyzerOutcome.Partial ? " (partial)" : string.Empty)));
        foreach (var finding in summary.HighestComplexityFunctions)
        {
            lines.AddRange(Wrap(
                $"  {finding.FunctionName} · {finding.RelativePath}:{finding.Line} · score {finding.Score}"));
        }
        AddAnalyzerExecution(lines, summary.Execution);
    }

    private static void AddDependencyResults(
        List<string> lines,
        JavaScriptDependencyAnalysis? analysis,
        bool dependencyGraphIncluded)
    {
        lines.Add("JAVASCRIPT/TYPESCRIPT DEPENDENCIES");
        if (analysis is null)
        {
            lines.Add("JavaScript/TypeScript dependency results unavailable");
            return;
        }

        if (analysis.Outcome == AnalyzerOutcome.NoSource)
        {
            lines.Add("No JavaScript or TypeScript source found");
            return;
        }

        if (analysis.ModuleCount is null ||
            analysis.DependencyCount is null ||
            analysis.CycleCount is null)
        {
            lines.Add(analysis.Outcome switch
            {
                AnalyzerOutcome.TimedOut => "Dependency analysis timed out",
                AnalyzerOutcome.ToolError => "Dependency analyzer error",
                _ => "Partial dependency results"
            });
            AddAnalyzerExecution(lines, analysis.Execution);
            return;
        }

        lines.AddRange(Wrap(
            $"{analysis.ModuleCount} modules · {analysis.DependencyCount} dependencies · " +
            $"{analysis.CycleCount} cycles" +
            (analysis.Outcome == AnalyzerOutcome.Partial ? " (partial)" : string.Empty)));
        if (analysis.MayBeIncomplete)
        {
            lines.Add("Some imports could not be resolved; the graph may be incomplete.");
        }

        for (var index = 0; index < analysis.Cycles.Count; index++)
        {
            var cycle = analysis.Cycles[index];
            var route = cycle.Count == 0
                ? "Unknown cycle"
                : string.Join(" -> ", cycle.Append(cycle[0]));
            lines.AddRange(Wrap($"Cycle {index + 1}: {route}"));
        }

        lines.Add(dependencyGraphIncluded
            ? "Dependency graph included on the final page."
            : "Dependency graph unavailable.");
        AddAnalyzerExecution(lines, analysis.Execution);
    }

    private static void AddAnalyzerExecution(List<string> lines, AnalyzerExecutionDetails? execution)
    {
        if (execution is null) return;
        lines.AddRange(Wrap($"  Tool: {execution.Tool} {execution.Version ?? "version unavailable"} · {execution.DurationMilliseconds} ms" +
                            (execution.ExitCode is null ? string.Empty : $" · exit {execution.ExitCode}")));
        if (!string.IsNullOrWhiteSpace(execution.ErrorReason))
            lines.AddRange(Wrap($"  Reason: {execution.ErrorReason}"));
    }

    private static string FormatTestResults(JobTestSummary? summary)
    {
        if (summary is null)
        {
            return "Test results unavailable";
        }

        var counts = summary.Executed is not null &&
                     summary.Passed is not null &&
                     summary.Failed is not null &&
                     summary.Skipped is not null
            ? $"{summary.Executed} executed · {summary.Passed} passed · {summary.Failed} failed · {summary.Skipped} skipped"
            : null;
        var status = summary.Outcome switch
        {
            TestRunOutcome.Completed => counts ?? "Test execution completed",
            TestRunOutcome.NoTestProjects => "No test projects",
            TestRunOutcome.ZeroTests => counts ?? "Zero tests discovered",
            TestRunOutcome.NotRun => JobResultSummary.TestSkipMessage(summary.SkipReason),
            TestRunOutcome.UnsupportedRunner => counts is null
                ? "Unsupported test runner"
                : $"{counts} (unsupported projects were not run)",
            TestRunOutcome.ExecutionError => counts is null
                ? "Test execution error"
                : $"{counts} (execution error)",
            TestRunOutcome.TimedOut => counts is null
                ? "Test execution timed out"
                : $"{counts} (timed out)",
            _ => "Test results unavailable"
        };

        return summary.IsPartial && !status.Contains("partial", StringComparison.OrdinalIgnoreCase)
            ? $"{status} (partial)"
            : status;
    }

    private static void AddField(List<string> lines, string label, string value)
    {
        lines.AddRange(Wrap($"{label}: {value}"));
    }

    private static string FormatDate(DateTime? value)
    {
        return value?.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture) ?? "Not available";
    }

    private static IEnumerable<string> Wrap(string value)
    {
        foreach (var rawParagraph in value.Replace("\r", string.Empty).Split('\n'))
        {
            var remaining = ToPdfAscii(rawParagraph).Trim();
            if (remaining.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (remaining.Length > MaximumLineLength)
            {
                var breakAt = remaining.LastIndexOf(' ', MaximumLineLength);
                if (breakAt <= 0)
                {
                    breakAt = MaximumLineLength;
                }

                yield return remaining[..breakAt].TrimEnd();
                remaining = remaining[breakAt..].TrimStart();
            }

            yield return remaining;
        }
    }

    private static byte[] BuildPdf(
        IReadOnlyList<string[]> pages,
        ReadOnlyMemory<byte>? dependencyGraphJpeg,
        ReadOnlyMemory<byte>? dotNetProjectGraphJpeg)
    {
        var graphs = new List<(string Title, ReadOnlyMemory<byte> Image, int Width, int Height)>();
        AddGraph("JavaScript/TypeScript Dependency Graph", dependencyGraphJpeg);
        AddGraph(".NET Project Dependency Graph", dotNetProjectGraphJpeg);
        var pageCount = pages.Count + graphs.Count;
        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n% CodeForge\n");

        var fontObjectId = 3 + (pageCount * 2);
        var firstImageObjectId = fontObjectId + 1;
        var objectCount = fontObjectId + graphs.Count;
        var offsets = new long[objectCount + 1];

        WriteObject(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");

        var pageReferences = string.Join(
            " ",
            Enumerable.Range(0, pageCount).Select(index => $"{3 + (index * 2)} 0 R"));
        WriteObject(
            stream,
            offsets,
            2,
            $"<< /Type /Pages /Kids [{pageReferences}] /Count {pageCount} >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var pageObjectId = 3 + (index * 2);
            var contentObjectId = pageObjectId + 1;
            var pageBody =
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObjectId} 0 R >> >> " +
                $"/Contents {contentObjectId} 0 R >>";
            WriteObject(stream, offsets, pageObjectId, pageBody);

            var content = BuildPageContent(pages[index], index + 1, pageCount);
            var contentLength = Encoding.ASCII.GetByteCount(content);
            WriteObject(
                stream,
                offsets,
                contentObjectId,
                $"<< /Length {contentLength} >>\nstream\n{content}endstream");
        }

        for (var graphIndex = 0; graphIndex < graphs.Count; graphIndex++)
        {
            var graph = graphs[graphIndex];
            var imageObjectId = firstImageObjectId + graphIndex;
            var pageObjectId = 3 + ((pages.Count + graphIndex) * 2);
            var contentObjectId = pageObjectId + 1;
            var pageBody =
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObjectId} 0 R >> " +
                $"/XObject << /Im1 {imageObjectId} 0 R >> >> " +
                $"/Contents {contentObjectId} 0 R >>";
            WriteObject(stream, offsets, pageObjectId, pageBody);

            var content = BuildGraphPageContent(
                graph.Title,
                graph.Width,
                graph.Height,
                pages.Count + graphIndex + 1,
                pageCount);
            WriteObject(
                stream,
                offsets,
                contentObjectId,
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        WriteObject(
            stream,
            offsets,
            fontObjectId,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        for (var graphIndex = 0; graphIndex < graphs.Count; graphIndex++)
        {
            var graph = graphs[graphIndex];
            var imageObjectId = firstImageObjectId + graphIndex;
            var image = graph.Image;
            WriteStreamObject(
                stream,
                offsets,
                imageObjectId,
                $"<< /Type /XObject /Subtype /Image /Width {graph.Width} " +
                $"/Height {graph.Height} /ColorSpace /DeviceRGB " +
                $"/BitsPerComponent 8 /Filter /DCTDecode /Length {image.Length} >>",
                image.Span);
        }

        var xrefOffset = stream.Position;
        WriteAscii(stream, $"xref\n0 {objectCount + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        for (var objectId = 1; objectId <= objectCount; objectId++)
        {
            WriteAscii(stream, $"{offsets[objectId]:D10} 00000 n \n");
        }

        WriteAscii(
            stream,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF\n");

        return stream.ToArray();

        void AddGraph(string title, ReadOnlyMemory<byte>? image)
        {
            if (!image.HasValue) return;
            var dimensions = TryReadJpegDimensions(image.Value.Span);
            if (dimensions is not null)
                graphs.Add((title, image.Value, dimensions.Value.Width, dimensions.Value.Height));
        }
    }

    private static string BuildGraphPageContent(
        string title,
        int imageWidth,
        int imageHeight,
        int pageNumber,
        int pageCount)
    {
        const double maximumWidth = 504;
        const double maximumHeight = 650;
        var scale = Math.Min(
            maximumWidth / imageWidth,
            maximumHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var x = (612 - width) / 2;
        var y = 54 + ((maximumHeight - height) / 2);

        return FormattableString.Invariant($$"""
            BT
            /F1 18 Tf
            54 744 Td
            ({{EscapePdfText(ToPdfAscii(title))}}) Tj
            /F1 9 Tf
            0 -22 Td
            (Page {{pageNumber}} of {{pageCount}}) Tj
            ET
            q
            {{width:0.###}} 0 0 {{height:0.###}} {{x:0.###}} {{y:0.###}} cm
            /Im1 Do
            Q
            """) + "\n";
    }

    private static (int Width, int Height)? TryReadJpegDimensions(
        ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
        {
            return null;
        }

        var index = 2;
        while (index + 8 < jpeg.Length)
        {
            while (index < jpeg.Length && jpeg[index] != 0xff)
            {
                index++;
            }

            while (index < jpeg.Length && jpeg[index] == 0xff)
            {
                index++;
            }

            if (index >= jpeg.Length)
            {
                break;
            }

            var marker = jpeg[index++];
            if (marker is 0xd9 or 0xda || index + 1 >= jpeg.Length)
            {
                break;
            }

            var length = (jpeg[index] << 8) + jpeg[index + 1];
            if (length < 2 || index + length > jpeg.Length)
            {
                return null;
            }

            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or
                0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or
                0xcd or 0xce or 0xcf)
            {
                var height = (jpeg[index + 3] << 8) + jpeg[index + 4];
                var width = (jpeg[index + 5] << 8) + jpeg[index + 6];
                return width > 0 && height > 0 ? (width, height) : null;
            }

            index += length;
        }

        return null;
    }

    private static string BuildPageContent(
        IReadOnlyList<string> lines,
        int pageNumber,
        int pageCount)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 18 Tf");
        content.AppendLine("54 744 Td");
        content.AppendLine("(CodeForge Build Report) Tj");
        content.AppendLine("/F1 9 Tf");
        content.AppendLine("0 -22 Td");
        content.AppendLine($"(Page {pageNumber} of {pageCount}) Tj");
        content.AppendLine("/F1 10 Tf");
        content.AppendLine("0 -26 Td");

        foreach (var line in lines)
        {
            content.Append('(')
                .Append(EscapePdfText(line))
                .AppendLine(") Tj");
            content.AppendLine("0 -14 Td");
        }

        content.AppendLine("ET");
        return content.ToString();
    }

    private static string ToPdfAscii(string value)
    {
        var characters = value.Select(character =>
            character is >= ' ' and <= '~' || character == '\u00b7'
                ? character
                : '?');
        return new string(characters.ToArray());
    }

    private static string EscapePdfText(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\u00b7", "\\267", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }

    private static void WriteObject(
        Stream stream,
        long[] offsets,
        int objectId,
        string body)
    {
        offsets[objectId] = stream.Position;
        WriteAscii(stream, $"{objectId} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteStreamObject(
        Stream stream,
        long[] offsets,
        int objectId,
        string dictionary,
        ReadOnlySpan<byte> content)
    {
        offsets[objectId] = stream.Position;
        WriteAscii(stream, $"{objectId} 0 obj\n{dictionary}\nstream\n");
        stream.Write(content);
        WriteAscii(stream, "\nendstream\nendobj\n");
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }
}

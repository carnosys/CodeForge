using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeForge.Infrastructure.Models;

namespace CodeForge.Worker;

internal static partial class CoberturaCoverageParser
{
    private const int MaximumReports = 200;
    private const long MaximumReportBytes = 20 * 1024 * 1024;
    private const int MaximumUncoveredLinesPerFile = 500;

    public static IReadOnlyList<FileCoverage> ParseAndCombine(
        IEnumerable<string> reportPaths,
        string repoPath)
    {
        var files = new Dictionary<string, MutableFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var reportPath in reportPaths
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(path => path, StringComparer.Ordinal)
                     .Where(path => new FileInfo(path).Length <= MaximumReportBytes)
                     .Take(MaximumReports))
        {
            var document = XDocument.Load(reportPath, LoadOptions.None);
            var sources = document.Descendants()
                .Where(element => element.Name.LocalName == "source")
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .ToArray();

            foreach (var classElement in document.Descendants()
                         .Where(element => element.Name.LocalName == "class"))
            {
                var filename = classElement.Attribute("filename")?.Value;
                var relativePath = ResolveRelativePath(filename, sources, repoPath);
                if (relativePath is null || IsGenerated(relativePath))
                {
                    continue;
                }

                if (!files.TryGetValue(relativePath, out var file))
                {
                    file = new MutableFile(relativePath);
                    files.Add(relativePath, file);
                }

                foreach (var lineElement in classElement.Descendants()
                             .Where(element => element.Name.LocalName == "line"))
                {
                    if (!int.TryParse(lineElement.Attribute("number")?.Value,
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) || line <= 0)
                    {
                        continue;
                    }

                    var covered = int.TryParse(lineElement.Attribute("hits")?.Value,
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits) && hits > 0;
                    file.Lines[line] = file.Lines.GetValueOrDefault(line) || covered;

                    var conditionCoverage = lineElement.Attribute("condition-coverage")?.Value;
                    var match = BranchCountsRegex().Match(conditionCoverage ?? string.Empty);
                    if (match.Success &&
                        int.TryParse(match.Groups["covered"].Value, out var coveredBranches) &&
                        int.TryParse(match.Groups["total"].Value, out var totalBranches))
                    {
                        var current = file.Branches.GetValueOrDefault(line);
                        file.Branches[line] = (
                            Math.Max(current.Covered, coveredBranches),
                            Math.Max(current.Total, totalBranches));
                    }
                }
            }
        }

        return files.Values
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => new FileCoverage
            {
                RelativePath = file.RelativePath,
                CoveredLines = file.Lines.Count(pair => pair.Value),
                MeasurableLines = file.Lines.Count,
                CoveredBranches = file.Branches.Count == 0
                    ? null
                    : file.Branches.Values.Sum(value => value.Covered),
                MeasurableBranches = file.Branches.Count == 0
                    ? null
                    : file.Branches.Values.Sum(value => value.Total),
                UncoveredLines = file.Lines
                    .Where(pair => !pair.Value)
                    .Select(pair => pair.Key)
                    .OrderBy(line => line)
                    .Take(MaximumUncoveredLinesPerFile)
                    .ToArray()
            })
            .ToArray();
    }

    public static IReadOnlyList<CoverageReviewPriority> Correlate(
        IReadOnlyList<FileCoverage> files,
        LanguageComplexitySummary complexity)
    {
        var byPath = files.ToDictionary(
            file => Normalize(file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        return complexity.FunctionsOverThresholdDetails
            .Select(function =>
            {
                if (function.EndLine < function.Line ||
                    !byPath.TryGetValue(Normalize(function.RelativePath), out var coverage))
                {
                    return null;
                }

                var uncovered = coverage.UncoveredLines
                    .Where(line => line >= function.Line && line <= function.EndLine)
                    .ToArray();
                return uncovered.Length == 0
                    ? null
                    : new CoverageReviewPriority
                    {
                        FunctionName = function.FunctionName,
                        RelativePath = function.RelativePath,
                        StartLine = function.Line,
                        EndLine = function.EndLine,
                        Complexity = function.Score,
                        UncoveredLines = uncovered
                    };
            })
            .Where(priority => priority is not null)
            .Cast<CoverageReviewPriority>()
            .OrderByDescending(priority => priority.Complexity)
            .ThenBy(priority => priority.RelativePath, StringComparer.Ordinal)
            .ThenBy(priority => priority.StartLine)
            .Take(20)
            .ToArray();
    }

    private static string? ResolveRelativePath(
        string? filename,
        IReadOnlyList<string> sources,
        string repoPath)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var candidates = new List<string>();
        if (Path.IsPathRooted(filename)) candidates.Add(filename);
        candidates.Add(Path.Combine(repoPath, filename));
        candidates.AddRange(sources.Select(source => Path.Combine(source, filename)));

        var root = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if ((fullPath.Equals(root, StringComparison.Ordinal) ||
                 fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) &&
                File.Exists(fullPath))
            {
                return Normalize(Path.GetRelativePath(root, fullPath));
            }
        }

        return null;
    }

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/bin/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    [GeneratedRegex(@"\((?<covered>\d+)\s*/\s*(?<total>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex BranchCountsRegex();

    private sealed class MutableFile(string relativePath)
    {
        public string RelativePath { get; } = relativePath;
        public Dictionary<int, bool> Lines { get; } = [];
        public Dictionary<int, (int Covered, int Total)> Branches { get; } = [];
    }
}

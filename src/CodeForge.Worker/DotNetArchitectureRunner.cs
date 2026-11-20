using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CodeForge.Infrastructure.Models;
using CodeForge.Infrastructure.Reports;

namespace CodeForge.Worker;

public sealed class DotNetArchitectureRunner
{
    public const string ConfigurationPath = ".codeforge/architecture.json";
    private const int MaximumGraphProjects = 80;
    private const int MaximumGraphEdges = 200;
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GraphvizTimeout = TimeSpan.FromSeconds(45);
    private readonly ProcessRunner _processRunner;
    private readonly IAnalysisArtifactStore _artifactStore;
    private readonly ILogger<DotNetArchitectureRunner> _logger;

    public DotNetArchitectureRunner(
        ProcessRunner processRunner,
        IAnalysisArtifactStore artifactStore,
        ILogger<DotNetArchitectureRunner> logger)
    {
        _processRunner = processRunner;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task<DotNetArchitectureAnalysis> AnalyzeAsync(
        Guid jobId,
        IReadOnlyList<string> projectFiles,
        string repoPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace);
        if (projectFiles.Count == 0)
        {
            return new DotNetArchitectureAnalysis { Outcome = ArchitectureAnalysisOutcome.NotApplicable };
        }

        var projectIds = projectFiles
            .Select(path => Normalize(Path.GetRelativePath(repoPath, path)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var projectSet = projectIds.ToHashSet(StringComparer.Ordinal);
        var references = new HashSet<(string Source, string Target)>();
        var limitations = new List<string>();
        var evaluatedCount = 0;

        foreach (var projectId in projectIds)
        {
            var projectPath = Path.GetFullPath(Path.Combine(repoPath, projectId));
            var evaluated = await TryGetEvaluatedReferencesAsync(projectPath, repoPath, cancellationToken);
            if (evaluated is not null)
            {
                evaluatedCount++;
                AddReferences(projectId, evaluated, projectSet, references, limitations);
                continue;
            }

            var literal = ReadLiteralReferences(projectPath, repoPath, limitations);
            AddReferences(projectId, literal, projectSet, references, limitations);
        }

        var (rulesConfigured, rules, configurationErrors) = ReadRules(repoPath, projectSet);
        var violations = rules
            .Where(rule => references.Contains((rule.Source, rule.Target)))
            .Select(rule => new ArchitectureViolation
            {
                RuleId = rule.Id,
                Source = rule.Source,
                Target = rule.Target,
                Description = rule.Description
            })
            .ToArray();
        var orderedReferences = references
            .Select(edge => new DotNetProjectReference { Source = edge.Source, Target = edge.Target })
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToArray();
        var graphTruncated = projectIds.Length > MaximumGraphProjects || orderedReferences.Length > MaximumGraphEdges;
        var graphAvailable = await TryRenderAndSaveGraphAsync(
            jobId, projectIds, orderedReferences, violations, workspace, cancellationToken);

        return new DotNetArchitectureAnalysis
        {
            Outcome = configurationErrors.Count > 0 || evaluatedCount != projectIds.Length
                ? ArchitectureAnalysisOutcome.Partial
                : ArchitectureAnalysisOutcome.Completed,
            DiscoveryMode = evaluatedCount == projectIds.Length
                ? ProjectReferenceDiscoveryMode.Evaluated
                : evaluatedCount == 0
                    ? ProjectReferenceDiscoveryMode.LiteralFallback
                    : ProjectReferenceDiscoveryMode.Mixed,
            Projects = projectIds.Select(path => new DotNetProjectNode { Path = path }).ToArray(),
            References = orderedReferences,
            RulesConfigured = rulesConfigured,
            Rules = rules,
            Violations = violations,
            ConfigurationErrors = configurationErrors,
            Limitations = limitations.Distinct(StringComparer.Ordinal).ToArray(),
            GraphAvailable = graphAvailable,
            GraphTruncated = graphTruncated
        };
    }

    private async Task<IReadOnlyList<string>?> TryGetEvaluatedReferencesAsync(
        string projectPath,
        string repoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                "dotnet",
                ["msbuild", projectPath, "-nologo", "-verbosity:quiet", "-getItem:ProjectReference", "-p:Configuration=Release"],
                repoPath,
                EvaluationTimeout,
                cancellationToken);
            if (result.TimeOut || result.ExitCode != 0) return null;
            var output = result.StandardOutput ?? string.Empty;
            var start = output.IndexOf('{');
            if (start < 0) return null;
            using var document = JsonDocument.Parse(output[start..]);
            if (!document.RootElement.TryGetProperty("Items", out var items) ||
                !items.TryGetProperty("ProjectReference", out var projectReferences))
            {
                return [];
            }

            return projectReferences.EnumerateArray()
                .Select(item => item.TryGetProperty("FullPath", out var fullPath)
                    ? fullPath.GetString()
                    : item.TryGetProperty("Identity", out var identity) ? identity.GetString() : null)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.IsPathRooted(path!)
                    ? path!
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, path!)))
                .Select(path => Normalize(Path.GetRelativePath(repoPath, path)))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static IReadOnlyList<string> ReadLiteralReferences(
        string projectPath,
        string repoPath,
        List<string> limitations)
    {
        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.None);
            var references = document.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .ToArray();
            if (references.Any(element => element.Attribute("Condition") is not null))
            {
                limitations.Add($"{Normalize(Path.GetRelativePath(repoPath, projectPath))}: conditional ProjectReference entries were read literally.");
            }
            if (document.Descendants().Any(element => element.Name.LocalName == "Import"))
            {
                limitations.Add($"{Normalize(Path.GetRelativePath(repoPath, projectPath))}: imported project references may be missing from the literal fallback graph.");
            }
            return references
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, value!)))
                .Select(path => Normalize(Path.GetRelativePath(repoPath, path)))
                .ToArray();
        }
        catch
        {
            limitations.Add($"{Normalize(Path.GetRelativePath(repoPath, projectPath))}: literal ProjectReference parsing failed.");
            return [];
        }
    }

    private static void AddReferences(
        string source,
        IEnumerable<string> targets,
        IReadOnlySet<string> projects,
        ISet<(string Source, string Target)> references,
        ICollection<string> limitations)
    {
        foreach (var target in targets.Distinct(StringComparer.Ordinal))
        {
            if (projects.Contains(target)) references.Add((source, target));
            else limitations.Add($"{source}: project reference '{target}' is outside the analyzed project set or could not be resolved.");
        }
    }

    private static (bool Configured, IReadOnlyList<ArchitectureRule> Rules, IReadOnlyList<string> Errors) ReadRules(
        string repoPath,
        IReadOnlySet<string> projects)
    {
        var path = Path.Combine(repoPath, ConfigurationPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return (false, [], []);
        var rules = new List<ArchitectureRule>();
        var errors = new List<string>();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024)
            {
                return (true, [], ["Architecture configuration exceeds the 1 MB limit."]);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("version", out var version) &&
                (!version.TryGetInt32(out var versionNumber) || versionNumber != 1))
            {
                return (true, [], ["Only architecture configuration version 1 is supported."]);
            }
            if (!document.RootElement.TryGetProperty("forbiddenDependencies", out var configuredRules) ||
                configuredRules.ValueKind != JsonValueKind.Array)
            {
                return (true, [], ["The architecture configuration must contain a forbiddenDependencies array."]);
            }
            var index = 0;
            foreach (var item in configuredRules.EnumerateArray())
            {
                index++;
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var rawSource = item.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() ?? "" : "";
                var rawTarget = item.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? "" : "";
                var source = Normalize(rawSource);
                var target = Normalize(rawTarget);
                var description = item.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) ||
                    !IsExplicitProjectPath(rawSource, source) ||
                    !IsExplicitProjectPath(rawTarget, target) ||
                    !projects.Contains(source) || !projects.Contains(target))
                {
                    errors.Add($"Rule {index} must have a non-empty id and source/target paths that exactly match analyzed projects.");
                    continue;
                }
                rules.Add(new ArchitectureRule { Id = id, Source = source, Target = target, Description = description });
            }
            foreach (var duplicate in rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                errors.Add($"Architecture rule id '{duplicate.Key}' is duplicated.");
            }
            if (errors.Any(error => error.Contains("duplicated", StringComparison.Ordinal)))
            {
                rules = rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
            }
        }
        catch (JsonException exception)
        {
            errors.Add($"Architecture configuration is invalid JSON: {exception.Message}");
        }
        return (true, rules, errors);
    }

    private async Task<bool> TryRenderAndSaveGraphAsync(
        Guid jobId,
        IReadOnlyList<string> projects,
        IReadOnlyList<DotNetProjectReference> references,
        IReadOnlyList<ArchitectureViolation> violations,
        string workspace,
        CancellationToken cancellationToken)
    {
        var shownProjects = projects.Take(MaximumGraphProjects).ToHashSet(StringComparer.Ordinal);
        var shownReferences = references
            .Where(edge => shownProjects.Contains(edge.Source) && shownProjects.Contains(edge.Target))
            .Take(MaximumGraphEdges)
            .ToArray();
        var violationEdges = violations.Select(value => (value.Source, value.Target)).ToHashSet();
        var dotPath = Path.Combine(workspace, "dotnet-projects.dot");
        var pngPath = Path.Combine(workspace, "dotnet-projects.png");
        var jpegPath = Path.Combine(workspace, "dotnet-projects.jpg");
        var dot = new StringBuilder("digraph projects {\n  rankdir=LR;\n  graph [bgcolor=\"white\"];\n  node [shape=box, style=\"rounded,filled\", fillcolor=\"#f3f1e9\", fontname=\"Helvetica\"];\n");
        foreach (var project in shownProjects.OrderBy(value => value, StringComparer.Ordinal))
            dot.Append("  \"").Append(Escape(project)).AppendLine("\";");
        foreach (var edge in shownReferences)
        {
            dot.Append("  \"").Append(Escape(edge.Source)).Append("\" -> \"").Append(Escape(edge.Target)).Append('"');
            if (violationEdges.Contains((edge.Source, edge.Target))) dot.Append(" [color=\"#c62828\", penwidth=3]");
            dot.AppendLine(";");
        }
        dot.AppendLine("}");
        await File.WriteAllTextAsync(dotPath, dot.ToString(), cancellationToken);
        try
        {
            var png = await _processRunner.RunAsync("dot", ["-Tpng", dotPath, "-o", pngPath], workspace, GraphvizTimeout, cancellationToken);
            var jpeg = await _processRunner.RunAsync("dot", ["-Tjpg", dotPath, "-o", jpegPath], workspace, GraphvizTimeout, cancellationToken);
            if (png.TimeOut || jpeg.TimeOut || png.ExitCode != 0 || jpeg.ExitCode != 0 || !File.Exists(pngPath) || !File.Exists(jpegPath)) return false;
            await _artifactStore.SaveDotNetProjectGraphAsync(jobId, await File.ReadAllBytesAsync(pngPath, cancellationToken), await File.ReadAllBytesAsync(jpegPath, cancellationToken), cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, ".NET project graph rendering or persistence failed for job {JobId}", jobId);
            return false;
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static bool IsExplicitProjectPath(string raw, string normalized) =>
        raw.Equals(normalized, StringComparison.Ordinal) &&
        !Path.IsPathRooted(raw) &&
        !normalized.Split('/').Contains("..", StringComparer.Ordinal) &&
        normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

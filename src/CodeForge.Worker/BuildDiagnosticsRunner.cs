using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using CodeForge.Infrastructure.Models;

namespace CodeForge.Worker;

public sealed class BuildDiagnosticsRunner
{
    public const string BuildConfiguration = "Release";
    public const string AnalysisProfile =
        "Repository build configuration with compiler and repository-configured analyzers; CodeForge adds no analyzer rules.";

    private const int MaximumReports = 500;
    private const int MaximumFindings = 5000;
    private const long MaximumReportBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private readonly ProcessRunner _processRunner;

    public BuildDiagnosticsRunner(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<BuildDiagnosticsResult> BuildAsync(
        string? solutionFile,
        IReadOnlyList<string> projectFiles,
        string repoPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        var sdkVersion = await GetSdkVersionAsync(repoPath, cancellationToken);
        var sarifRoot = Path.Combine(workspace, "build-findings");
        Directory.CreateDirectory(sarifRoot);
        var targetsPath = Path.Combine(workspace, "CodeForge.BuildDiagnostics.targets");
        var allProjects = Directory
            .EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(repoPath, path), StringComparer.Ordinal)
            .ToArray();
        await WriteTargetsAsync(targetsPath, allProjects, cancellationToken);

        IReadOnlyList<string> buildTargets = solutionFile is null
            ? projectFiles
            : [solutionFile];
        var buildSucceeded = true;
        var timedOut = false;
        string? failureReason = null;

        foreach (var target in buildTargets)
        {
            var result = await _processRunner.RunAsync(
                "dotnet",
                [
                    "build",
                    target,
                    "--configuration",
                    BuildConfiguration,
                    "--nologo",
                    $"-p:CodeForgeSarifRoot={sarifRoot}",
                    $"-p:CustomAfterMicrosoftCommonTargets={targetsPath}"
                ],
                repoPath,
                BuildTimeout,
                cancellationToken);

            if (result.TimeOut)
            {
                buildSucceeded = false;
                timedOut = true;
                failureReason = $"The .NET build timed out after {BuildTimeout.TotalMinutes:0} minutes.";
                break;
            }

            if (result.ExitCode != 0)
            {
                buildSucceeded = false;
                failureReason =
                    $"The .NET build failed with exit code {result.ExitCode}. Raw build output is kept out of the public job record because repository builds are untrusted.";
                break;
            }
        }

        var collection = CollectReports(sarifRoot, repoPath);
        var outcome = collection.ReportCount == 0
            ? buildSucceeded
                ? BuildFindingsOutcome.Unsupported
                : BuildFindingsOutcome.Unavailable
            : !buildSucceeded || collection.WasPartial || timedOut
                ? BuildFindingsOutcome.Partial
                : BuildFindingsOutcome.Completed;

        return new BuildDiagnosticsResult(
            new BuildFindingsAnalysis
            {
                Outcome = outcome,
                BuildSucceeded = buildSucceeded,
                SdkVersion = sdkVersion,
                BuildConfiguration = BuildConfiguration,
                AnalysisProfile = AnalysisProfile,
                AdditionalRulesEnabled = false,
                Findings = collection.Findings
            },
            failureReason);
    }

    public static BuildFindingsAnalysis NotApplicable(string? sdkVersion = null)
    {
        return new BuildFindingsAnalysis
        {
            Outcome = BuildFindingsOutcome.NotApplicable,
            BuildSucceeded = null,
            SdkVersion = sdkVersion,
            BuildConfiguration = BuildConfiguration,
            AnalysisProfile = AnalysisProfile,
            AdditionalRulesEnabled = false
        };
    }

    public static BuildFindingsAnalysis Unavailable(string? sdkVersion = null)
    {
        return new BuildFindingsAnalysis
        {
            Outcome = BuildFindingsOutcome.Unavailable,
            BuildSucceeded = null,
            SdkVersion = sdkVersion,
            BuildConfiguration = BuildConfiguration,
            AnalysisProfile = AnalysisProfile,
            AdditionalRulesEnabled = false
        };
    }

    private async Task<string?> GetSdkVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                "dotnet",
                ["--version"],
                workingDirectory,
                VersionTimeout,
                cancellationToken);
            if (!result.TimeOut && result.ExitCode == 0)
            {
                return result.StandardOutput?.Trim();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The build itself will provide the public failure state.
        }

        return null;
    }

    private static async Task WriteTargetsAsync(
        string path,
        IReadOnlyList<string> projects,
        CancellationToken cancellationToken)
    {
        var root = new XElement("Project");
        for (var index = 0; index < projects.Count; index++)
        {
            var escapedPath = Path.GetFullPath(projects[index])
                .Replace("'", "%27", StringComparison.Ordinal);
            root.Add(
                new XElement(
                    "PropertyGroup",
                    new XAttribute(
                        "Condition",
                        $"'$(MSBuildProjectFullPath)' == '{escapedPath}'"),
                    new XElement("CodeForgeProjectIndex", index.ToString("D4", CultureInfo.InvariantCulture))));
        }

        root.Add(
            new XElement(
                "Target",
                new XAttribute("Name", "CodeForgeConfigureBuildDiagnostics"),
                new XAttribute("BeforeTargets", "CoreCompile"),
                new XElement(
                    "PropertyGroup",
                    new XAttribute("Condition", "'$(CodeForgeProjectIndex)' != ''"),
                    new XElement(
                        "CodeForgeFrameworkKey",
                        new XAttribute("Condition", "'$(TargetFramework)' == ''"),
                        "not-specified"),
                    new XElement(
                        "CodeForgeFrameworkKey",
                        new XAttribute("Condition", "'$(TargetFramework)' != ''"),
                        "$(TargetFramework)"),
                    new XElement(
                        "CodeForgeSarifDirectory",
                        "$(CodeForgeSarifRoot)/$(CodeForgeProjectIndex)/$(CodeForgeFrameworkKey)"),
                    new XElement(
                        "ErrorLog",
                        "$(CodeForgeSarifDirectory)/diagnostics.sarif,version=2.1")),
                new XElement(
                    "MakeDir",
                    new XAttribute("Directories", "$(CodeForgeSarifDirectory)"),
                    new XAttribute("Condition", "'$(CodeForgeProjectIndex)' != ''"))));

        var document = new XDocument(root);
        await File.WriteAllTextAsync(
            path,
            document.ToString(SaveOptions.DisableFormatting),
            cancellationToken);
    }

    private static SarifCollection CollectReports(string sarifRoot, string repoPath)
    {
        var reports = Directory
            .EnumerateFiles(sarifRoot, "*.sarif", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var wasPartial = reports.Length > MaximumReports;
        var findings = new List<BuildFinding>();
        var deduplicationKeys = new HashSet<string>(StringComparer.Ordinal);
        var parsedReports = 0;

        foreach (var report in reports.Take(MaximumReports))
        {
            try
            {
                var file = new FileInfo(report);
                if (file.Length > MaximumReportBytes)
                {
                    wasPartial = true;
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllBytes(report));
                ParseSarifDocument(
                    document.RootElement,
                    repoPath,
                    findings,
                    deduplicationKeys,
                    ref wasPartial);
                parsedReports++;
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                wasPartial = true;
            }
        }

        return new SarifCollection(
            findings
                .OrderBy(finding => SeverityOrder(finding.ReportedSeverity))
                .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                .ThenBy(finding => finding.Line)
                .ThenBy(finding => finding.Column)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            parsedReports,
            wasPartial);
    }

    private static void ParseSarifDocument(
        JsonElement root,
        string repoPath,
        List<BuildFinding> findings,
        HashSet<string> deduplicationKeys,
        ref bool wasPartial)
    {
        if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            wasPartial = true;
            return;
        }

        foreach (var run in runs.EnumerateArray())
        {
            var rules = ReadRules(run);
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (findings.Count >= MaximumFindings)
                {
                    wasPartial = true;
                    return;
                }

                var rule = ResolveRule(result, rules);
                var ruleId = ReadString(result, "ruleId") ?? rule?.Id ?? "Unknown";
                var severity = ReadString(result, "level") ?? rule?.DefaultLevel ?? "warning";
                var message = result.TryGetProperty("message", out var messageElement)
                    ? ReadString(messageElement, "text") ?? ReadString(messageElement, "markdown")
                    : null;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "The compiler or analyzer did not provide a diagnostic message.";
                }

                var helpLink = ValidateHelpLink(rule?.HelpUri);
                var locations = ReadLocations(result).ToArray();
                if (locations.Length == 0)
                {
                    AddFinding(
                        findings,
                        deduplicationKeys,
                        ruleId,
                        severity,
                        message,
                        null,
                        null,
                        null,
                        helpLink);
                    continue;
                }

                foreach (var location in locations)
                {
                    AddFinding(
                        findings,
                        deduplicationKeys,
                        ruleId,
                        severity,
                        message,
                        ToRepositoryRelativePath(location.Uri, repoPath),
                        location.Line,
                        location.Column,
                        helpLink);
                }
            }
        }
    }

    private static IReadOnlyList<SarifRule> ReadRules(JsonElement run)
    {
        if (!run.TryGetProperty("tool", out var tool) ||
            !tool.TryGetProperty("driver", out var driver) ||
            !driver.TryGetProperty("rules", out var rulesElement) ||
            rulesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rulesElement.EnumerateArray()
            .Select(rule => new SarifRule(
                ReadString(rule, "id") ?? "Unknown",
                ReadString(rule, "helpUri"),
                rule.TryGetProperty("defaultConfiguration", out var configuration)
                    ? ReadString(configuration, "level")
                    : null))
            .ToArray();
    }

    private static SarifRule? ResolveRule(
        JsonElement result,
        IReadOnlyList<SarifRule> rules)
    {
        if (result.TryGetProperty("rule", out var ruleReference) &&
            ruleReference.TryGetProperty("index", out var indexElement) &&
            indexElement.TryGetInt32(out var index) &&
            index >= 0 && index < rules.Count)
        {
            return rules[index];
        }

        var ruleId = ReadString(result, "ruleId");
        return ruleId is null
            ? null
            : rules.FirstOrDefault(rule => rule.Id.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<SarifLocation> ReadLocations(JsonElement result)
    {
        if (!result.TryGetProperty("locations", out var locations) ||
            locations.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var location in locations.EnumerateArray())
        {
            if (!location.TryGetProperty("physicalLocation", out var physicalLocation))
            {
                continue;
            }

            string? uri = null;
            if (physicalLocation.TryGetProperty("artifactLocation", out var artifactLocation))
            {
                uri = ReadString(artifactLocation, "uri");
            }

            int? line = null;
            int? column = null;
            if (physicalLocation.TryGetProperty("region", out var region))
            {
                if (region.TryGetProperty("startLine", out var lineElement) &&
                    lineElement.TryGetInt32(out var parsedLine))
                {
                    line = parsedLine;
                }

                if (region.TryGetProperty("startColumn", out var columnElement) &&
                    columnElement.TryGetInt32(out var parsedColumn))
                {
                    column = parsedColumn;
                }
            }

            yield return new SarifLocation(uri, line, column);
        }
    }

    private static void AddFinding(
        List<BuildFinding> findings,
        HashSet<string> deduplicationKeys,
        string ruleId,
        string severity,
        string message,
        string? relativePath,
        int? line,
        int? column,
        string? helpLink)
    {
        var normalizedSeverity = severity.Trim().ToLowerInvariant();
        var key = string.Join(
            '\u001f',
            ruleId,
            normalizedSeverity,
            message,
            relativePath ?? string.Empty,
            line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            column?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        if (!deduplicationKeys.Add(key))
        {
            return;
        }

        findings.Add(new BuildFinding
        {
            RuleId = ruleId,
            ReportedSeverity = normalizedSeverity,
            Message = message,
            RelativePath = relativePath,
            Line = line,
            Column = column,
            HelpLink = helpLink
        });
    }

    private static string? ToRepositoryRelativePath(string? value, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return null;
            }

            path = uri.LocalPath;
        }
        else
        {
            path = Uri.UnescapeDataString(value).Replace('/', Path.DirectorySeparatorChar);
            path = Path.Combine(repoPath, path);
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.Equals(root, StringComparison.Ordinal) &&
            !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static string? ValidateHelpLink(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
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

    private sealed record SarifRule(
        string Id,
        string? HelpUri,
        string? DefaultLevel);

    private sealed record SarifLocation(
        string? Uri,
        int? Line,
        int? Column);

    private sealed record SarifCollection(
        IReadOnlyList<BuildFinding> Findings,
        int ReportCount,
        bool WasPartial);
}

public sealed record BuildDiagnosticsResult(
    BuildFindingsAnalysis Analysis,
    string? FailureReason);

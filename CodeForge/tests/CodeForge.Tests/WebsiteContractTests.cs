using System.Runtime.CompilerServices;
using Xunit;

namespace CodeForge.Tests;

public sealed class WebsiteContractTests
{
    [Fact]
    public void LandingPageDescribesRepositoryAnalysisInsteadOfOnlyBuildExecution()
    {
        var html = ReadWebAsset("index.html");

        Assert.Contains("Understand code.", html);
        Assert.Contains("Improve what matters.", html);
        Assert.Contains("diagnostics, tests, coverage, complexity, and dependency structure", html);
        Assert.DoesNotContain(".NET BUILD RUNNER", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ship code that", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubmissionFormCollectsRepositoryAndExplicitTrustedTestOptIn()
    {
        var html = ReadWebAsset("index.html");
        var script = ReadWebAsset("app.js");

        Assert.Contains("name=\"repoUrl\"", html);
        Assert.Contains("name=\"runTests\"", html);
        Assert.Contains("Only enable this for a repository you trust", html);
        Assert.DoesNotContain("name=\"jobTitle\"", html);
        Assert.DoesNotContain("name=\"jobDescription\"", html);
        Assert.Contains("payload.runTests = data.get(\"runTests\") === \"true\"", script);
        Assert.Contains("fetch(apiUrl", script);
    }

    [Fact]
    public void AnalysisPageIncludesEveryPersistedEngineeringSignal()
    {
        var script = ReadWebAsset("analysis.js");

        var expectedSections = new[]
        {
            "Build Findings",
            ".NET Project Dependencies",
            "Test Results",
            "Test Coverage",
            "Code Complexity",
            "JavaScript/TypeScript Dependencies"
        };

        foreach (var section in expectedSections)
        {
            Assert.Contains(section, script);
        }
    }

    [Fact]
    public void DependencyDiagramFrontendAndApiUseTheSameRoute()
    {
        var script = ReadWebAsset("analysis.js");
        var controller = ReadSource("src", "CodeForge.Api", "Controllers", "JobController.cs");

        Assert.Contains("if (getValue(analysis, \"graphAvailable\"))", script);
        Assert.Contains("/api/job/${encodeURIComponent(getValue(job, \"id\"))}/dependency-graph", script);
        Assert.Contains("graph.loading = \"lazy\"", script);
        Assert.Contains("[HttpGet(\"{id}/dependency-graph\")]", controller);
        Assert.Contains("File(graph.Value.ToArray(), \"image/png\")", controller);
    }

    [Fact]
    public void FrontendDoesNotInjectRepositoryControlledHtml()
    {
        var scripts = ReadWebAsset("app.js") + ReadWebAsset("analysis.js");

        Assert.DoesNotContain(".innerHTML", scripts, StringComparison.Ordinal);
        Assert.DoesNotContain(".outerHTML", scripts, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", scripts, StringComparison.Ordinal);
        Assert.Contains("textContent = getRepositoryName", scripts);
        Assert.Contains("message.textContent = getValue(finding, \"message\")", scripts);
    }

    private static string ReadWebAsset(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CodeForge.Api", "wwwroot", fileName));

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}

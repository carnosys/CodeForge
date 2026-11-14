const panel = document.querySelector("#analysis-panel");
const statusNames = ["Pending", "Running", "Completed", "Failed"];
const testOutcomeNames = [
  "Completed",
  "NoTestProjects",
  "ZeroTests",
  "NotRun",
  "UnsupportedRunner",
  "ExecutionError",
  "TimedOut",
  "NotApplicable"
];
const analyzerOutcomeNames = ["Completed", "NoSource", "Partial", "TimedOut", "ToolError", "ToolUnavailable", "ExecutionFailed", "MalformedOutput"];
const buildFindingsOutcomeNames = ["Completed", "NotApplicable", "Partial", "Unsupported", "Unavailable"];
const coverageOutcomeNames = ["Completed", "Partial", "NotRun", "NoTestProjects", "UnsupportedCollector", "Unavailable", "NotApplicable"];
const architectureOutcomeNames = ["Completed", "Partial", "NotApplicable", "ToolError"];
const projectReferenceModeNames = ["Evaluated", "LiteralFallback", "Mixed"];
const testSkipReasonNames = ["None", "TrustedRepositoryOptInMissing", "WorkerFeatureFlagDisabled", "BothPrerequisitesMissing", "BuildFailed", "ProjectDiscoveryFailed"];
let refreshTimer;

function getValue(object, key) {
  return object[key] ?? object[key[0].toUpperCase() + key.slice(1)];
}

function getStatus(job) {
  const value = getValue(job, "status");
  return typeof value === "number" ? statusNames[value] ?? "Unknown" : value;
}

function formatDate(value, fallback = "Not available") {
  if (!value) return fallback;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return fallback;
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "medium"
  }).format(date);
}

function getRepositoryName(repoUrl) {
  try {
    const path = new URL(repoUrl).pathname.replace(/^\/+|\/+$/g, "");
    return path || repoUrl;
  } catch {
    return repoUrl;
  }
}

function formatDuration(startedAt, completedAt) {
  if (!startedAt) return "Not started";
  const start = new Date(startedAt);
  const end = completedAt ? new Date(completedAt) : new Date();
  const seconds = Math.max(0, Math.round((end - start) / 1000));
  if (!Number.isFinite(seconds)) return "Unavailable";
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return minutes < 60 ? `${minutes}m ${remainder}s` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

function enumName(value, names) {
  return typeof value === "number" ? names[value] ?? "Unknown" : value;
}

function createSignal(label, value, detail, tone = "neutral") {
  const item = document.createElement("article");
  item.className = `analysis-signal signal-${tone}`;
  const signalLabel = document.createElement("span");
  signalLabel.textContent = label;
  const signalValue = document.createElement("strong");
  signalValue.textContent = value;
  const signalDetail = document.createElement("p");
  signalDetail.textContent = detail;
  item.append(signalLabel, signalValue, signalDetail);
  return item;
}

function createSignalOverview(job) {
  const section = document.createElement("section");
  section.className = "analysis-overview";
  const heading = document.createElement("div");
  heading.className = "analysis-section-heading";
  const headingText = document.createElement("div");
  const kicker = document.createElement("p");
  kicker.className = "eyebrow";
  kicker.textContent = "AT A GLANCE";
  const title = document.createElement("h3");
  title.textContent = "Repository health signals";
  const copy = document.createElement("p");
  copy.textContent = "A compact reading of the persisted evidence. Open each section below for the supporting detail.";
  headingText.append(kicker, title, copy);
  heading.append(headingText);

  const signals = document.createElement("div");
  signals.className = "analysis-signal-grid";
  const build = getValue(job, "buildFindings");
  const buildSucceeded = build && getValue(build, "buildSucceeded");
  signals.append(createSignal(
    "Build",
    buildSucceeded === true ? "Passed" : buildSucceeded === false ? "Failed" : "Pending",
    build ? `${getValue(build, "buildConfiguration") || "Configured"} · .NET ${getValue(build, "sdkVersion") || "SDK unavailable"}` : "No structured build result yet.",
    buildSucceeded === true ? "good" : buildSucceeded === false ? "danger" : "muted"));

  const tests = getValue(job, "testResults");
  const testOutcome = tests ? enumName(getValue(tests, "outcome"), testOutcomeNames) : null;
  const testFailed = tests && getValue(tests, "failed");
  const testExecuted = tests && getValue(tests, "executed");
  const testPending = !getValue(job, "reportReady") && ["Pending", "Running"].includes(getStatus(job));
  signals.append(createSignal(
    "Tests",
    testPending ? "Pending" : Number.isInteger(testExecuted) ? `${testExecuted} executed` : testOutcome === "NotRun" ? "Not run" : testOutcome || "Pending",
    Number.isInteger(testFailed) ? `${getValue(tests, "passed")} passed · ${testFailed} failed · ${getValue(tests, "skipped")} skipped` : "No completed test counts available.",
    Number.isInteger(testFailed) ? (testFailed > 0 ? "danger" : "good") : "muted"));

  const coverage = getValue(job, "coverage");
  const linePercentage = coverage && getValue(coverage, "linePercentage");
  signals.append(createSignal(
    "Line coverage",
    typeof linePercentage === "number" ? `${linePercentage.toFixed(1)}%` : "Unavailable",
    coverage && Number.isInteger(getValue(coverage, "measurableLines"))
      ? `${getValue(coverage, "coveredLines")} of ${getValue(coverage, "measurableLines")} measurable lines covered.`
      : "Coverage requires supported test execution.",
    typeof linePercentage === "number" ? "neutral" : "muted"));

  const complexity = getValue(job, "codeComplexity");
  const languages = complexity
    ? [getValue(complexity, "cSharp"), getValue(complexity, "javaScriptTypeScript")].filter(Boolean)
    : [];
  const functions = languages.reduce((total, language) => total + (getValue(language, "functionsAnalyzed") || 0), 0);
  const overThreshold = languages.reduce((total, language) => total + (getValue(language, "functionsOverThreshold") || 0), 0);
  signals.append(createSignal(
    "Complexity",
    languages.length ? `${overThreshold} high-risk` : "Pending",
    languages.length ? `${functions} functions analyzed against threshold 15.` : "No Lizard result yet.",
    languages.length ? (overThreshold > 0 ? "warning" : "good") : "muted"));

  const dependencies = getValue(job, "javaScriptDependencies");
  const cycles = dependencies && getValue(dependencies, "cycleCount");
  signals.append(createSignal(
    "JS/TS cycles",
    Number.isInteger(cycles) ? String(cycles) : "Not applicable",
    dependencies && Number.isInteger(getValue(dependencies, "moduleCount"))
      ? `${getValue(dependencies, "moduleCount")} modules · ${getValue(dependencies, "dependencyCount")} dependency edges.`
      : "No JavaScript/TypeScript dependency graph available.",
    Number.isInteger(cycles) ? (cycles > 0 ? "danger" : "good") : "muted"));

  const architecture = getValue(job, "dotNetArchitecture");
  const violations = architecture ? getValue(architecture, "violations") || [] : [];
  const rulesConfigured = architecture && Boolean(getValue(architecture, "rulesConfigured"));
  signals.append(createSignal(
    "Architecture rules",
    rulesConfigured ? `${violations.length} violation${violations.length === 1 ? "" : "s"}` : "Not configured",
    architecture ? `${(getValue(architecture, "projects") || []).length} .NET projects discovered.` : "No project architecture result yet.",
    rulesConfigured ? (violations.length ? "danger" : "good") : "muted"));

  section.append(heading, signals);
  return section;
}

function createSectionIntro(kickerText, titleText, copyText) {
  const heading = document.createElement("div");
  heading.className = "analysis-section-heading analysis-group-heading";
  const kicker = document.createElement("p");
  kicker.className = "eyebrow";
  kicker.textContent = kickerText;
  const title = document.createElement("h3");
  title.textContent = titleText;
  const copy = document.createElement("p");
  copy.textContent = copyText;
  heading.append(kicker, title, copy);
  return heading;
}

function appendDetail(grid, label, value, options = {}) {
  const item = document.createElement("div");
  item.className = "analysis-detail";

  const term = document.createElement("dt");
  term.textContent = label;
  const description = document.createElement("dd");

  if (options.link) {
    const link = document.createElement("a");
    link.href = value;
    link.target = "_blank";
    link.rel = "noreferrer";
    link.textContent = value;
    description.append(link);
  } else {
    description.textContent = value;
  }

  item.append(term, description);
  grid.append(item);
}

function showLookupError(title, detail) {
  const state = document.createElement("div");
  state.className = "lookup-error";

  const marker = document.createElement("span");
  marker.className = "lookup-error-code";
  marker.textContent = "LOOKUP FAILED";
  const heading = document.createElement("h2");
  heading.textContent = title;
  const copy = document.createElement("p");
  copy.textContent = detail;
  const link = document.createElement("a");
  link.className = "primary-link";
  link.href = "/";
  link.textContent = "Return to build queue →";

  state.append(marker, heading, copy, link);
  panel.replaceChildren(state);
}

function createReportActions(job) {
  const jobId = getValue(job, "id");
  const reportReady = Boolean(getValue(job, "reportReady"));
  const actions = document.createElement("div");
  actions.className = "analysis-summary-actions";

  const button = document.createElement("button");
  button.type = "button";
  button.className = "save-report-button";
  button.textContent = "Save report PDF";
  button.disabled = !reportReady;

  const message = document.createElement("span");
  message.className = "report-download-message";
  message.setAttribute("aria-live", "polite");
  if (!reportReady) message.textContent = "Available after final processing completes.";

  button.addEventListener("click", async () => {
    button.disabled = true;
    button.textContent = "Saving report…";
    message.textContent = "";

    try {
      const response = await fetch(`/api/job/${encodeURIComponent(jobId)}/report`);
      if (!response.ok) {
        let detail = `The server returned ${response.status}.`;
        try {
          const problem = await response.json();
          detail = problem.detail || problem.title || detail;
        } catch {
          // The response was not a problem-details document.
        }
        throw new Error(detail);
      }

      const report = await response.blob();
      const downloadUrl = URL.createObjectURL(report);
      const download = document.createElement("a");
      download.href = downloadUrl;
      download.download = `codeforge-report-${jobId.replaceAll("-", "")}.pdf`;
      document.body.append(download);
      download.click();
      download.remove();
      URL.revokeObjectURL(downloadUrl);
      message.textContent = "Report saved to Blob Storage and downloaded.";
    } catch (error) {
      message.textContent = error.message;
    } finally {
      button.disabled = !reportReady;
      button.textContent = "Save report PDF";
    }
  });

  actions.append(button, message);
  return actions;
}

function createTestResults(job) {
  const summary = getValue(job, "testResults");
  const section = document.createElement("section");
  section.className = "test-results";

  const label = document.createElement("span");
  label.textContent = "Test Results";
  const heading = document.createElement("h3");
  const copy = document.createElement("p");

  if (!getValue(job, "reportReady") && ["Pending", "Running"].includes(getStatus(job))) {
    heading.textContent = "Awaiting test stage";
    copy.textContent = "Test execution and its exact skip reason are reported after repository discovery and build complete.";
    section.append(label, heading, copy);
    return section;
  }

  if (!summary) {
    heading.textContent = "Test results unavailable";
    copy.textContent = "This job predates structured test reporting or has not finished processing.";
    section.append(label, heading, copy);
    return section;
  }

  const rawOutcome = getValue(summary, "outcome");
  const outcome = typeof rawOutcome === "number"
    ? testOutcomeNames[rawOutcome] ?? "Unknown"
    : rawOutcome;
  const executed = getValue(summary, "executed");
  const passed = getValue(summary, "passed");
  const failed = getValue(summary, "failed");
  const skipped = getValue(summary, "skipped");
  const partial = Boolean(getValue(summary, "isPartial"));
  const hasCounts = [executed, passed, failed, skipped]
    .every(value => Number.isInteger(value));

  if (hasCounts) {
    heading.textContent = `${executed} executed · ${passed} passed · ${failed} failed · ${skipped} skipped`;
    if (failed > 0) section.classList.add("has-failures");
  }

  const messages = {
    Completed: failed > 0
      ? "The test run completed with failed assertions; the repository analysis still completed successfully."
      : "The test run completed successfully.",
    NoTestProjects: "No VSTest-compatible test projects were discovered in the selected solution or project set.",
    ZeroTests: "Test projects ran, but the runner reported zero discovered tests.",
    NotRun: {
      TrustedRepositoryOptInMissing: "Tests were skipped because this job was not opted in as a trusted repository.",
      WorkerFeatureFlagDisabled: "Tests were skipped because the worker test-execution feature flag is disabled.",
      BothPrerequisitesMissing: "Tests were skipped because trusted-repository opt-in and the worker feature flag are both missing.",
      BuildFailed: "Tests were skipped because the repository build failed.",
      ProjectDiscoveryFailed: "Tests were skipped because .NET project discovery failed."
    }[typeof getValue(summary, "skipReason") === "number" ? testSkipReasonNames[getValue(summary, "skipReason")] : getValue(summary, "skipReason")] || "Tests were skipped because execution prerequisites were not met.",
    UnsupportedRunner: "One or more test projects use an unsupported runner. Initial support is limited to Microsoft.NET.Test.Sdk/VSTest projects.",
    ExecutionError: "The test runner crashed or did not produce a valid TRX report.",
    TimedOut: "The test run exceeded its bounded execution timeout.",
    NotApplicable: "The repository has no .NET project, so .NET build and test execution are not applicable."
  };

  if (!hasCounts) {
    heading.textContent = {
      NoTestProjects: "No test projects",
      ZeroTests: "Zero tests discovered",
      NotRun: "Tests not run",
      UnsupportedRunner: "Unsupported test runner",
      ExecutionError: "Test execution error",
      TimedOut: "Test execution timed out",
      NotApplicable: ".NET tests not applicable"
    }[outcome] || "Test results unavailable";
  }

  copy.textContent = `${messages[outcome] || "The test outcome is unavailable."}${partial ? " Available counts are partial." : ""}`;
  section.append(label, heading, copy);
  return section;
}

function formatCoveragePercentage(value) {
  return typeof value === "number" ? `${value.toFixed(1)}%` : "unavailable";
}

function createCoverageResults(job) {
  const coverage = getValue(job, "coverage");
  const section = document.createElement("section");
  section.className = "analyzer-results coverage-results";
  const label = document.createElement("span");
  label.textContent = "Test Coverage";
  const heading = document.createElement("h3");
  section.append(label, heading);

  if (!coverage) {
    heading.textContent = "Coverage unavailable";
    const copy = document.createElement("p");
    copy.textContent = "This job predates structured coverage reporting or has not finished processing.";
    section.append(copy);
    return section;
  }

  const rawOutcome = getValue(coverage, "outcome");
  const outcome = typeof rawOutcome === "number"
    ? coverageOutcomeNames[rawOutcome] ?? "Unknown"
    : rawOutcome;
  const linePercentage = getValue(coverage, "linePercentage");
  const branchPercentage = getValue(coverage, "branchPercentage");
  const coveredLines = getValue(coverage, "coveredLines");
  const measurableLines = getValue(coverage, "measurableLines");
  const coveredBranches = getValue(coverage, "coveredBranches");
  const measurableBranches = getValue(coverage, "measurableBranches");

  if ((outcome === "Completed" || outcome === "Partial") && Number.isInteger(measurableLines)) {
    heading.textContent = `Line coverage ${formatCoveragePercentage(linePercentage)}` +
      (Number.isInteger(measurableBranches) ? ` · Branch coverage ${formatCoveragePercentage(branchPercentage)}` : "");
    const counts = document.createElement("p");
    counts.textContent = `${coveredLines} of ${measurableLines} measurable lines covered` +
      (Number.isInteger(measurableBranches) ? ` · ${coveredBranches} of ${measurableBranches} branches covered` : " · Branch coverage unavailable") +
      (outcome === "Partial" ? " · Partial collection" : "");
    section.append(counts);
  } else {
    const states = {
      NotRun: "Coverage not collected",
      NoTestProjects: "No test projects",
      UnsupportedCollector: "Coverage collector unsupported",
      Unavailable: "Coverage unavailable",
      NotApplicable: ".NET coverage not applicable"
    };
    heading.textContent = states[outcome] || "Coverage unavailable";
  }

  const explanation = document.createElement("p");
  explanation.textContent = "Coverage measures which application code was exercised by tests; it does not prove that the behavior is correct.";
  section.append(explanation);

  const files = (getValue(coverage, "files") || [])
    .filter(file => (getValue(file, "uncoveredLines") || []).length > 0)
    .sort((left, right) => (getValue(left, "linePercentage") ?? 101) - (getValue(right, "linePercentage") ?? 101));
  if (files.length > 0) {
    const title = document.createElement("h4");
    title.textContent = "Files with uncovered lines";
    const list = document.createElement("ul");
    list.className = "coverage-files";
    files.forEach(file => {
      const item = document.createElement("li");
      const path = document.createElement("strong");
      path.textContent = `${getValue(file, "relativePath")} · ${formatCoveragePercentage(getValue(file, "linePercentage"))}`;
      const evidence = document.createElement("span");
      const lines = getValue(file, "uncoveredLines") || [];
      evidence.textContent = `Uncovered lines: ${lines.slice(0, 30).join(", ")}${lines.length > 30 ? "…" : ""}`;
      item.append(path, evidence);
      list.append(item);
    });
    section.append(title, list);
  }

  const priorities = getValue(coverage, "reviewPriorities") || [];
  if (priorities.length > 0) {
    const title = document.createElement("h4");
    title.textContent = "Complexity and coverage review priorities";
    const copy = document.createElement("p");
    copy.textContent = "These functions exceed the configured complexity threshold and have uncovered executable lines within a supported source range.";
    const list = document.createElement("ul");
    list.className = "coverage-priorities";
    priorities.forEach(priority => {
      const item = document.createElement("li");
      const name = document.createElement("strong");
      name.textContent = `${getValue(priority, "functionName")} · complexity ${getValue(priority, "complexity")}`;
      const location = document.createElement("span");
      location.textContent = `${getValue(priority, "relativePath")}:${getValue(priority, "startLine")}-${getValue(priority, "endLine")} · uncovered lines ${(getValue(priority, "uncoveredLines") || []).join(", ")}`;
      item.append(name, location);
      list.append(item);
    });
    section.append(title, copy, list);
  }

  const limitations = getValue(coverage, "limitations") || [];
  if (limitations.length > 0) {
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "Collection details and limitations";
    const list = document.createElement("ul");
    limitations.forEach(value => {
      const item = document.createElement("li");
      item.textContent = value;
      list.append(item);
    });
    const checkout = document.createElement("p");
    checkout.textContent = getValue(coverage, "checkoutModified")
      ? "The submitted checkout was temporarily modified for collection."
      : "The submitted checkout was not modified; CodeForge used a temporary worker-owned runsettings file.";
    details.append(summary, list, checkout);
    section.append(details);
  }

  return section;
}

function createDotNetArchitecture(job) {
  const analysis = getValue(job, "dotNetArchitecture");
  const section = document.createElement("section");
  section.className = "analyzer-results dotnet-architecture";
  const label = document.createElement("span");
  label.textContent = ".NET Project Dependencies";
  const heading = document.createElement("h3");
  section.append(label, heading);
  if (!analysis) {
    heading.textContent = ".NET project dependency results unavailable";
    const copy = document.createElement("p");
    copy.textContent = "This job predates structured .NET architecture reporting or has not finished processing.";
    section.append(copy);
    return section;
  }
  const rawOutcome = getValue(analysis, "outcome");
  const outcome = typeof rawOutcome === "number" ? architectureOutcomeNames[rawOutcome] ?? "Unknown" : rawOutcome;
  if (outcome === "NotApplicable") {
    heading.textContent = "No .NET projects found";
    return section;
  }
  const projects = getValue(analysis, "projects") || [];
  const references = getValue(analysis, "references") || [];
  const rawMode = getValue(analysis, "discoveryMode");
  const mode = typeof rawMode === "number" ? projectReferenceModeNames[rawMode] ?? "Unknown" : rawMode;
  heading.textContent = `${projects.length} projects · ${references.length} direct references`;
  const metadata = document.createElement("p");
  metadata.textContent = `Reference discovery: ${mode}${outcome === "Partial" ? " · Partial results" : ""}. Arrows point from the referencing project to the project it depends on.`;
  section.append(metadata);

  const bySource = new Map(projects.map(project => [getValue(project, "path"), []]));
  references.forEach(edge => {
    const source = getValue(edge, "source");
    if (!bySource.has(source)) bySource.set(source, []);
    bySource.get(source).push(getValue(edge, "target"));
  });
  const list = document.createElement("ul");
  list.className = "project-reference-list";
  [...bySource.entries()].sort(([a], [b]) => a.localeCompare(b)).forEach(([source, targets]) => {
    const item = document.createElement("li");
    const sourceNode = document.createElement("strong");
    sourceNode.textContent = source;
    const targetNode = document.createElement("span");
    targetNode.textContent = targets.length ? `depends on ${targets.sort().join(", ")}` : "no project references";
    item.append(sourceNode, targetNode);
    list.append(item);
  });
  section.append(list);

  const rulesConfigured = Boolean(getValue(analysis, "rulesConfigured"));
  const violations = getValue(analysis, "violations") || [];
  const ruleHeading = document.createElement("h4");
  ruleHeading.textContent = "Architecture rules";
  const ruleCopy = document.createElement("p");
  if (!rulesConfigured) {
    ruleCopy.textContent = "Skipped: no architecture rules configuration supplied.";
  } else if (violations.length === 0) {
    ruleCopy.textContent = "No direct project references violate the valid configured rules.";
  } else {
    ruleCopy.textContent = `${violations.length} forbidden direct project reference${violations.length === 1 ? "" : "s"} detected.`;
    section.classList.add("has-architecture-violations");
  }
  section.append(ruleHeading, ruleCopy);
  if (violations.length) {
    const violationsList = document.createElement("ul");
    violationsList.className = "architecture-violations";
    violations.forEach(violation => {
      const item = document.createElement("li");
      item.textContent = `${getValue(violation, "ruleId")}: ${getValue(violation, "source")} → ${getValue(violation, "target")}${getValue(violation, "description") ? ` · ${getValue(violation, "description")}` : ""}`;
      violationsList.append(item);
    });
    section.append(violationsList);
  }
  const errors = getValue(analysis, "configurationErrors") || [];
  const limitations = getValue(analysis, "limitations") || [];
  if (errors.length || limitations.length) {
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "Configuration and discovery details";
    const detailList = document.createElement("ul");
    [...errors.map(value => `Configuration error: ${value}`), ...limitations].forEach(value => {
      const item = document.createElement("li"); item.textContent = value; detailList.append(item);
    });
    details.append(summary, detailList); section.append(details);
  }
  if (getValue(analysis, "graphTruncated")) {
    const warning = document.createElement("p");
    warning.className = "analyzer-warning";
    warning.textContent = "The image is a bounded overview; the textual dependency list is complete.";
    section.append(warning);
  }
  if (getValue(analysis, "graphAvailable")) {
    const image = document.createElement("img");
    image.className = "dependency-graph";
    image.src = `/api/job/${encodeURIComponent(getValue(job, "id"))}/dotnet-project-graph`;
    image.alt = ".NET project dependency graph; arrows point from referencing projects to dependencies";
    section.append(image);
  }
  return section;
}

function createBuildFindings(job) {
  const analysis = getValue(job, "buildFindings");
  const section = document.createElement("section");
  section.className = "build-findings";
  const label = document.createElement("span");
  label.textContent = "Build Findings";
  const heading = document.createElement("h3");
  section.append(label, heading);

  if (!analysis) {
    heading.textContent = "Build findings unavailable";
    const copy = document.createElement("p");
    copy.textContent = "This job predates structured compiler and analyzer reporting or has not finished processing.";
    section.append(copy);
    return section;
  }

  const rawOutcome = getValue(analysis, "outcome");
  const outcome = typeof rawOutcome === "number"
    ? buildFindingsOutcomeNames[rawOutcome] ?? "Unknown"
    : rawOutcome;
  const findings = getValue(analysis, "findings") || [];
  const counts = getValue(analysis, "severityCounts") || {};
  const severityOrder = ["error", "warning", "note", "info", "none", "hidden"];
  const countParts = Object.entries(counts)
    .sort(([left], [right]) => {
      const leftIndex = severityOrder.indexOf(left.toLowerCase());
      const rightIndex = severityOrder.indexOf(right.toLowerCase());
      return (leftIndex < 0 ? 99 : leftIndex) - (rightIndex < 0 ? 99 : rightIndex) || left.localeCompare(right);
    })
    .map(([severity, count]) => `${count} ${severity}`);

  if (outcome === "NotApplicable") {
    heading.textContent = ".NET build findings not applicable";
  } else if (outcome === "Unsupported") {
    heading.textContent = "Structured diagnostic collection unsupported";
  } else if (outcome === "Unavailable") {
    heading.textContent = "Build findings unavailable";
  } else if (findings.length === 0) {
    heading.textContent = outcome === "Partial"
      ? "No findings were recovered from the partial report"
      : "No compiler or analyzer findings";
  } else {
    heading.textContent = `${findings.length} findings${countParts.length ? ` · ${countParts.join(" · ")}` : ""}${outcome === "Partial" ? " · partial" : ""}`;
  }

  const metadata = document.createElement("p");
  metadata.className = "build-findings-metadata";
  const sdk = getValue(analysis, "sdkVersion") || "unknown SDK";
  const configuration = getValue(analysis, "buildConfiguration") || "unknown configuration";
  const profile = getValue(analysis, "analysisProfile") || "Analysis profile unavailable.";
  const additionalRules = getValue(analysis, "additionalRulesEnabled")
    ? "CodeForge additional rules were enabled."
    : "No CodeForge analyzer rules were added.";
  metadata.textContent = `.NET SDK ${sdk} · ${configuration}. ${profile} ${additionalRules}`;
  section.append(metadata);

  if (outcome === "Partial" || outcome === "Unavailable" || outcome === "Unsupported") {
    const state = document.createElement("p");
    state.className = "analyzer-warning";
    state.textContent = {
      Partial: "Only the diagnostics recovered before the build stopped are shown.",
      Unsupported: "The build completed without producing supported SARIF output.",
      Unavailable: "The build did not produce a readable structured diagnostic report."
    }[outcome];
    section.append(state);
  }

  if (findings.length) {
    const list = document.createElement("div");
    list.className = "build-finding-list";
    for (const finding of findings) {
      const item = document.createElement("article");
      item.className = "build-finding";
      const itemHeading = document.createElement("div");
      itemHeading.className = "build-finding-heading";
      const rule = document.createElement("strong");
      rule.textContent = getValue(finding, "ruleId") || "Unknown rule";
      const severity = document.createElement("span");
      severity.className = `finding-severity severity-${String(getValue(finding, "reportedSeverity") || "unknown").toLowerCase()}`;
      severity.textContent = getValue(finding, "reportedSeverity") || "unknown";
      itemHeading.append(rule, severity);

      const location = document.createElement("code");
      const relativePath = getValue(finding, "relativePath");
      const line = getValue(finding, "line");
      const column = getValue(finding, "column");
      location.textContent = relativePath
        ? `${relativePath}${Number.isInteger(line) ? `:${line}${Number.isInteger(column) ? `:${column}` : ""}` : ""}`
        : "Location unavailable";

      const message = document.createElement("p");
      message.className = "build-finding-message";
      message.textContent = getValue(finding, "message") || "Diagnostic message unavailable.";
      item.append(itemHeading, location, message);

      const explanationText = getValue(finding, "explanation");
      if (explanationText && explanationText !== getValue(finding, "message")) {
        const explanation = document.createElement("p");
        explanation.className = "build-finding-guidance";
        explanation.textContent = `Explanation: ${explanationText}`;
        item.append(explanation);
      }

      const actionText = getValue(finding, "suggestedAction");
      if (actionText) {
        const action = document.createElement("p");
        action.className = "build-finding-guidance";
        action.textContent = `Suggested action: ${actionText}`;
        item.append(action);
      }

      const helpLink = getValue(finding, "helpLink");
      if (helpLink) {
        try {
          const url = new URL(helpLink);
          if (url.protocol === "https:" || url.protocol === "http:") {
            const link = document.createElement("a");
            link.href = url.href;
            link.target = "_blank";
            link.rel = "noreferrer";
            link.textContent = "Official rule documentation ↗";
            item.append(link);
          }
        } catch {
          // Invalid or unsafe analyzer-controlled help link.
        }
      }

      list.append(item);
    }
    section.append(list);
  }

  return section;
}

function getAnalyzerOutcome(summary) {
  const value = getValue(summary, "outcome");
  return typeof value === "number"
    ? analyzerOutcomeNames[value] ?? "Unknown"
    : value;
}

function appendAnalyzerExecution(block, summary) {
  const execution = getValue(summary, "execution");
  if (!execution) return;
  const metadata = document.createElement("p");
  metadata.className = "analyzer-execution";
  const exitCode = getValue(execution, "exitCode");
  metadata.textContent = `${getValue(execution, "tool")} ${getValue(execution, "version") || "version unavailable"} · ${getValue(execution, "durationMilliseconds")} ms${Number.isInteger(exitCode) ? ` · exit ${exitCode}` : ""}`;
  block.append(metadata);
  const reason = getValue(execution, "errorReason");
  if (reason) {
    const error = document.createElement("p");
    error.className = "analyzer-warning";
    error.textContent = reason;
    block.append(error);
  }
}

function appendComplexityLanguage(section, label, summary) {
  const block = document.createElement("div");
  block.className = "analyzer-language";
  const heading = document.createElement("h4");
  heading.textContent = label;
  const copy = document.createElement("p");
  const outcome = getAnalyzerOutcome(summary);
  const functions = getValue(summary, "functionsAnalyzed");
  const overThreshold = getValue(summary, "functionsOverThreshold");

  if (outcome === "NoSource") {
    copy.textContent = "No applicable source found";
  } else if (Number.isInteger(functions) && Number.isInteger(overThreshold)) {
    copy.textContent = `${functions} functions analyzed · ${overThreshold} over complexity threshold 15${outcome === "Partial" ? " · partial results" : ""}`;
  } else {
    copy.textContent = {
      TimedOut: "Complexity analysis timed out",
      ToolError: "Complexity analyzer error",
      ToolUnavailable: "Complexity tool unavailable",
      ExecutionFailed: "Complexity analyzer execution failed",
      MalformedOutput: "Complexity analyzer returned malformed output"
    }[outcome] || "Complexity results unavailable";
  }

  block.append(heading, copy);
  const findings = getValue(summary, "highestComplexityFunctions") || [];
  if (findings.length) {
    const list = document.createElement("ol");
    list.className = "analyzer-findings";
    for (const finding of findings) {
      const item = document.createElement("li");
      const name = document.createElement("strong");
      name.textContent = getValue(finding, "functionName");
      const location = document.createElement("span");
      location.textContent = `${getValue(finding, "relativePath")}:${getValue(finding, "line")} · score ${getValue(finding, "score")}`;
      item.append(name, location);
      list.append(item);
    }
    block.append(list);
  }
  appendAnalyzerExecution(block, summary);
  section.append(block);
}

function createComplexityResults(job) {
  const analysis = getValue(job, "codeComplexity");
  const section = document.createElement("section");
  section.className = "analyzer-results";
  const label = document.createElement("span");
  label.textContent = "Code Complexity";
  const title = document.createElement("h3");
  title.textContent = analysis
    ? "Lizard complexity findings"
    : "Code complexity results unavailable";
  section.append(label, title);

  if (!analysis) {
    const copy = document.createElement("p");
    copy.textContent = "This job predates structured complexity reporting or has not finished processing.";
    section.append(copy);
    return section;
  }

  appendComplexityLanguage(section, "C#", getValue(analysis, "cSharp"));
  appendComplexityLanguage(
    section,
    "JavaScript / TypeScript",
    getValue(analysis, "javaScriptTypeScript"));
  return section;
}

function createDependencyResults(job) {
  const analysis = getValue(job, "javaScriptDependencies");
  const section = document.createElement("section");
  section.className = "analyzer-results dependency-results";
  const label = document.createElement("span");
  label.textContent = "JavaScript/TypeScript Dependencies";
  const title = document.createElement("h3");
  section.append(label, title);

  if (!analysis) {
    title.textContent = "Dependency results unavailable";
    const copy = document.createElement("p");
    copy.textContent = "This job predates structured dependency reporting or has not finished processing.";
    section.append(copy);
    return section;
  }

  const outcome = getAnalyzerOutcome(analysis);
  if (outcome === "NoSource") {
    title.textContent = "No JavaScript or TypeScript source found";
    return section;
  }

  const modules = getValue(analysis, "moduleCount");
  const dependencies = getValue(analysis, "dependencyCount");
  const cycleCount = getValue(analysis, "cycleCount");
  if ([modules, dependencies, cycleCount].every(Number.isInteger)) {
    title.textContent = `${modules} modules · ${dependencies} dependencies · ${cycleCount} cycles`;
  } else {
    title.textContent = {
      TimedOut: "Dependency analysis timed out",
      ToolError: "Dependency analyzer error",
      ToolUnavailable: "Dependency tool unavailable",
      ExecutionFailed: "Dependency analyzer execution failed",
      MalformedOutput: "Dependency analyzer returned malformed output",
      Partial: "Partial dependency results"
    }[outcome] || "Dependency results unavailable";
  }

  if (getValue(analysis, "mayBeIncomplete")) {
    const warning = document.createElement("p");
    warning.className = "analyzer-warning";
    warning.textContent = "Some imports could not be resolved; the graph may be incomplete.";
    section.append(warning);
  }

  const cycles = getValue(analysis, "cycles") || [];
  if (cycles.length) {
    const list = document.createElement("ol");
    list.className = "dependency-cycles";
    cycles.forEach((cycle, index) => {
      const item = document.createElement("li");
      const route = cycle.length ? [...cycle, cycle[0]].join(" → ") : "Unknown cycle";
      item.textContent = `Cycle ${index + 1}: ${route}`;
      list.append(item);
    });
    section.append(list);
  }

  if (getValue(analysis, "graphAvailable")) {
    const graph = document.createElement("img");
    graph.className = "dependency-graph";
    graph.src = `/api/job/${encodeURIComponent(getValue(job, "id"))}/dependency-graph`;
    graph.alt = "JavaScript and TypeScript dependency graph";
    graph.loading = "lazy";
    section.append(graph);
  }

  appendAnalyzerExecution(section, analysis);

  return section;
}

function renderJob(job) {
  const status = getStatus(job);
  const failureReason = getValue(job, "failureReason");

  const summary = document.createElement("div");
  summary.className = "analysis-summary";
  const summaryText = document.createElement("div");
  const eyebrow = document.createElement("p");
  eyebrow.className = "eyebrow";
  eyebrow.textContent = "REPOSITORY";
  const title = document.createElement("h2");
  title.textContent = getRepositoryName(getValue(job, "repoUrl"));
  summaryText.append(eyebrow, title);

  const pill = document.createElement("span");
  pill.className = `status-pill status-${String(status).toLowerCase()}`;
  pill.textContent = status;
  const summaryActions = createReportActions(job);
  summaryActions.prepend(pill);
  summary.append(summaryText, summaryActions);

  const grid = document.createElement("dl");
  grid.className = "analysis-grid";
  appendDetail(grid, "Job ID", getValue(job, "id"));
  appendDetail(grid, "Repository", getValue(job, "repoUrl"), { link: true });
  appendDetail(grid, "Queued", formatDate(getValue(job, "createdAt")));
  appendDetail(grid, "Started", formatDate(getValue(job, "startedAt"), "Not started"));
  appendDetail(grid, "Finished", formatDate(getValue(job, "completedAt"), "Not finished"));
  appendDetail(grid, "Duration", formatDuration(getValue(job, "startedAt"), getValue(job, "completedAt")));
  appendDetail(grid, "Repository commit", getValue(job, "repositoryCommitSha") || "Unavailable");
  appendDetail(grid, "Worker version", getValue(job, "workerVersion") || "Unavailable");

  const result = document.createElement("section");
  const resultSummary = getValue(job, "resultSummary");
  const resultLabelText = getValue(resultSummary || {}, "label") || "";
  result.className = `analysis-result ${status === "Failed" ? "is-failed" : resultLabelText.includes("errors") || resultLabelText.includes("failure") ? "is-warning" : ""}`;
  const resultLabel = document.createElement("span");
  resultLabel.textContent = "Processing summary";
  const resultTitle = document.createElement("h3");
  resultTitle.textContent = getValue(resultSummary || {}, "label") || `This build is ${String(status).toLowerCase()}.`;
  const resultCopy = document.createElement("div");
  resultCopy.className = "analysis-result-copy";
  const summaryLines = [getValue(resultSummary || {}, "description") || failureReason || "Lifecycle details unavailable."];
  if (status === "Completed" || status === "Failed") {
    (getValue(resultSummary || {}, "failedStages") || []).forEach(value => summaryLines.push(`Needs attention: ${value}`));
    (getValue(resultSummary || {}, "skippedStages") || []).forEach(value => summaryLines.push(value));
  }
  summaryLines.forEach((value, index) => {
    const line = document.createElement(index === 0 ? "p" : "li");
    line.textContent = value;
    if (index === 0) resultCopy.append(line);
    else {
      let list = resultCopy.querySelector("ul");
      if (!list) { list = document.createElement("ul"); resultCopy.append(list); }
      list.append(line);
    }
  });
  result.append(resultLabel, resultTitle, resultCopy);

  const qualityGrid = document.createElement("div");
  qualityGrid.className = "analysis-section-grid";
  qualityGrid.append(createBuildFindings(job), createTestResults(job), createCoverageResults(job), createComplexityResults(job));

  const structureGrid = document.createElement("div");
  structureGrid.className = "analysis-section-grid analysis-structure-grid";
  structureGrid.append(createDotNetArchitecture(job), createDependencyResults(job));

  panel.replaceChildren(
    summary,
    createSignalOverview(job),
    grid,
    result,
    createSectionIntro("QUALITY", "Build, tests, and maintainability", "Compiler evidence and code-quality signals, separated from infrastructure outcomes."),
    qualityGrid,
    createSectionIntro("STRUCTURE", "Dependencies and architecture", "Project relationships, JavaScript and TypeScript imports, cycles, and configured architecture rules."),
    structureGrid);
}

async function loadAnalysis() {
  const id = new URLSearchParams(window.location.search).get("id")?.trim();
  const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

  if (!id || !guidPattern.test(id)) {
    showLookupError(
      "Job ID is missing or incorrect",
      "Open the analysis page from a job in the build queue, or provide a valid job ID in the page URL."
    );
    return;
  }

  try {
    const response = await fetch(`/api/job/${encodeURIComponent(id)}`);
    if (response.status === 404) {
      showLookupError(
        "Job does not exist",
        `No job exists with ID ${id}. It may have been deleted.`
      );
      return;
    }

    if (!response.ok) {
      throw new Error(`The server returned ${response.status}.`);
    }

    const job = await response.json();
    renderJob(job);
    const status = getStatus(job);
    if (!getValue(job, "reportReady") && (status === "Pending" || status === "Running")) {
      clearTimeout(refreshTimer);
      refreshTimer = setTimeout(loadAnalysis, 3000);
    }
  } catch (error) {
    showLookupError(
      "Could not load job analysis",
      `${error.message} Check the API connection and try again.`
    );
  }
}

loadAnalysis();

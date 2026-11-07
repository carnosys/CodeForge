const panel = document.querySelector("#analysis-panel");
const statusNames = ["Pending", "Running", "Completed", "Failed"];

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

function renderJob(job) {
  const status = getStatus(job);
  const failureReason = getValue(job, "failureReason");

  const summary = document.createElement("div");
  summary.className = "analysis-summary";
  const summaryText = document.createElement("div");
  const eyebrow = document.createElement("p");
  eyebrow.className = "eyebrow";
  eyebrow.textContent = "BUILD STATUS";
  const title = document.createElement("h2");
  title.textContent = getValue(job, "jobTitle") || "Untitled build";
  summaryText.append(eyebrow, title);

  const pill = document.createElement("span");
  pill.className = `status-pill status-${String(status).toLowerCase()}`;
  pill.textContent = status;
  summary.append(summaryText, pill);

  const grid = document.createElement("dl");
  grid.className = "analysis-grid";
  appendDetail(grid, "Job ID", getValue(job, "id"));
  appendDetail(grid, "Repository", getValue(job, "repoUrl"), { link: true });
  appendDetail(grid, "Description", getValue(job, "jobDescription") || "No description");
  appendDetail(grid, "Queued", formatDate(getValue(job, "createdAt")));
  appendDetail(grid, "Started", formatDate(getValue(job, "startedAt"), "Not started"));
  appendDetail(grid, "Finished", formatDate(getValue(job, "completedAt"), "Not finished"));

  const result = document.createElement("section");
  result.className = `analysis-result ${status === "Failed" ? "is-failed" : ""}`;
  const resultLabel = document.createElement("span");
  resultLabel.textContent = status === "Failed" ? "Failure reason" : "Analysis";
  const resultTitle = document.createElement("h3");
  resultTitle.textContent = status === "Failed" ? "Why this build failed" : `This build is ${String(status).toLowerCase()}.`;
  const resultCopy = document.createElement("pre");
  resultCopy.textContent = status === "Failed"
    ? failureReason || "The worker did not provide a failure reason for this older job. Retry it to capture a detailed result."
    : status === "Completed"
      ? "The repository was cloned and the .NET project built successfully."
      : "No failure has been recorded. Refresh this page after processing finishes.";
  result.append(resultLabel, resultTitle, resultCopy);

  panel.replaceChildren(summary, grid, result);
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

    renderJob(await response.json());
  } catch (error) {
    showLookupError(
      "Could not load job analysis",
      `${error.message} Check the API connection and try again.`
    );
  }
}

loadAnalysis();

const apiUrl = "/api/job";
const form = document.querySelector("#job-form");
const list = document.querySelector("#job-list");
const template = document.querySelector("#job-template");
const submitButton = document.querySelector("#submit-button");
const refreshButton = document.querySelector("#refresh-button");
const message = document.querySelector("#form-message");

const statusNames = ["Pending", "Running", "Completed", "Failed"];

function getValue(object, key) {
  return object[key] ?? object[key[0].toUpperCase() + key.slice(1)];
}

function getStatus(job) {
  const value = getValue(job, "status");
  return typeof value === "number" ? statusNames[value] ?? "Unknown" : value;
}

function formatTime(value) {
  if (!value) return "Just now";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Just now";
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(date);
}

function showMessage(text, isSuccess = false) {
  message.textContent = text;
  message.classList.toggle("success", isSuccess);
}

async function getError(response) {
  const text = await response.text();
  if (!text) return `Request failed (${response.status})`;
  try {
    const body = JSON.parse(text);
    return body.title || body.detail || text;
  } catch {
    return text;
  }
}

function renderJobs(jobs) {
  list.replaceChildren();

  if (!jobs.length) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = "No builds yet. Queue your first repository above.";
    list.append(empty);
    return;
  }

  jobs
    .sort((a, b) => new Date(getValue(b, "createdAt")) - new Date(getValue(a, "createdAt")))
    .forEach((job, index) => {
      const card = template.content.firstElementChild.cloneNode(true);
      const title = getValue(job, "jobTitle") || "Untitled build";
      const repoUrl = getValue(job, "repoUrl");
      const status = getStatus(job);

      card.style.animationDelay = `${index * 35}ms`;
      card.querySelector(".job-index").textContent = String(index + 1).padStart(2, "0");
      card.querySelector("h3").textContent = title;
      card.querySelector(".job-description").textContent = getValue(job, "jobDescription") || "No description";

      const link = card.querySelector(".repo-link");
      link.href = repoUrl;
      link.textContent = repoUrl.replace(/^https:\/\//, "");

      const pill = card.querySelector(".status-pill");
      pill.textContent = status;
      pill.classList.add(`status-${status.toLowerCase()}`);

      card.querySelector(".job-time").textContent = formatTime(getValue(job, "createdAt"));
      card.querySelector(".delete-button").addEventListener("click", () => deleteJob(getValue(job, "id"), card));
      list.append(card);
    });
}

async function loadJobs() {
  refreshButton.classList.add("loading");
  try {
    const response = await fetch(apiUrl);
    if (!response.ok) throw new Error(await getError(response));
    renderJobs(await response.json());
  } catch (error) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = `Could not load builds. ${error.message}`;
    list.replaceChildren(empty);
  } finally {
    refreshButton.classList.remove("loading");
  }
}

async function deleteJob(id, card) {
  const button = card.querySelector(".delete-button");
  button.disabled = true;
  button.textContent = "Deleting…";
  try {
    const response = await fetch(`${apiUrl}/${id}`, { method: "DELETE" });
    if (!response.ok) throw new Error(await getError(response));
    card.remove();
    if (!list.children.length) renderJobs([]);
  } catch (error) {
    button.disabled = false;
    button.textContent = "Delete";
    showMessage(error.message);
  }
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  showMessage("");
  submitButton.disabled = true;
  submitButton.firstElementChild.textContent = "Queuing…";

  const data = new FormData(form);
  const payload = Object.fromEntries(data.entries());

  try {
    const response = await fetch(apiUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error(await getError(response));
    form.reset();
    showMessage("Build queued successfully.", true);
    await loadJobs();
  } catch (error) {
    showMessage(error.message);
  } finally {
    submitButton.disabled = false;
    submitButton.firstElementChild.textContent = "Queue build";
  }
});

refreshButton.addEventListener("click", loadJobs);
loadJobs();
setInterval(loadJobs, 10000);

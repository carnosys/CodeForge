function safeReturnUrl() {
  const value = new URLSearchParams(window.location.search).get("returnUrl");
  return value && value.startsWith("/") && !value.startsWith("//")
    ? value
    : "/";
}

const authStage = document.querySelector("#auth-form-stage");
const authForms = {
  login: document.querySelector("#login-form"),
  register: document.querySelector("#register-form")
};

function showAuthForm(mode) {
  const selectedForm = authForms[mode];
  if (!selectedForm) return;

  Object.values(authForms).forEach(form => {
    form.hidden = form !== selectedForm;
  });
  authStage.hidden = false;
  selectedForm.querySelector("input")?.focus({ preventScroll: true });
  authStage.scrollIntoView({ behavior: "smooth", block: "center" });
}

function closeAuthForms() {
  authStage.hidden = true;
  Object.values(authForms).forEach(form => { form.hidden = true; });
}

async function readError(response) {
  const body = await response.json().catch(() => null);
  if (!body) return `Request failed (${response.status}).`;
  if (body.errors) return Object.values(body.errors).flat().join(" ");
  return body.detail || body.title || `Request failed (${response.status}).`;
}

async function submitAuthForm(form, endpoint) {
  const button = form.querySelector("button[type='submit']");
  const message = form.querySelector("[data-auth-message]");
  const data = new FormData(form);
  button.disabled = true;
  message.textContent = "";

  try {
    const response = await fetch(`/api/auth/${endpoint}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email: data.get("email"), password: data.get("password") })
    });
    if (!response.ok) throw new Error(await readError(response));
    window.location.assign(safeReturnUrl());
  } catch (error) {
    message.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

document.querySelector("#login-form").addEventListener("submit", event => {
  event.preventDefault();
  submitAuthForm(event.currentTarget, "login");
});

document.querySelector("#register-form").addEventListener("submit", event => {
  event.preventDefault();
  submitAuthForm(event.currentTarget, "register");
});

document.querySelectorAll("[data-show-auth]").forEach(button => {
  button.addEventListener("click", () => showAuthForm(button.dataset.showAuth));
});

document.querySelectorAll("[data-close-auth]").forEach(button => {
  button.addEventListener("click", closeAuthForms);
});

const requestedMode = new URLSearchParams(window.location.search).get("mode");
if (requestedMode === "login" || requestedMode === "register") {
  showAuthForm(requestedMode);
}

fetch("/api/auth/me").then(response => {
  if (response.ok) window.location.replace(safeReturnUrl());
});

function safeReturnUrl() {
  const value = new URLSearchParams(window.location.search).get("returnUrl");
  return value && value.startsWith("/") && !value.startsWith("//")
    ? value
    : "/";
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

fetch("/api/auth/me").then(response => {
  if (response.ok) window.location.replace(safeReturnUrl());
});

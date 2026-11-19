const authApiUrl = "/api/auth";

function redirectToLogin() {
  const returnUrl = `${window.location.pathname}${window.location.search}`;
  window.location.assign(`/login.html?returnUrl=${encodeURIComponent(returnUrl)}`);
}

async function getCurrentUser() {
  const response = await fetch(`${authApiUrl}/me`);
  if (response.status === 401) return null;
  if (!response.ok) throw new Error(`Authentication check failed (${response.status}).`);
  return response.json();
}

async function requireUser() {
  try {
    const user = await getCurrentUser();
    if (!user) {
      redirectToLogin();
      return null;
    }

    document.querySelectorAll("[data-user-email]").forEach(element => {
      element.textContent = user.email;
    });
    return user;
  } catch {
    redirectToLogin();
    return null;
  }
}

async function logout() {
  await fetch(`${authApiUrl}/logout`, { method: "POST" });
  window.location.assign("/login.html");
}

document.querySelectorAll("[data-logout]").forEach(button => {
  button.addEventListener("click", logout);
});

window.CodeForgeAuth = { getCurrentUser, requireUser, redirectToLogin, logout };

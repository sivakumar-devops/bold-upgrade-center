(() => {
    const toggle = document.querySelector(".standalone-password-toggle");
    const password = document.getElementById("Password");
    if (!toggle || !password) return;

    toggle.hidden = false;
    toggle.addEventListener("click", () => {
        const visible = password.type === "password";
        password.type = visible ? "text" : "password";
        const label = visible ? "Hide password" : "Show password";
        toggle.setAttribute("aria-pressed", String(visible));
        toggle.setAttribute("aria-label", label);
        toggle.title = label;
    });
})();

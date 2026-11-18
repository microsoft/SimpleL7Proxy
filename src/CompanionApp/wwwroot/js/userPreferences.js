// Minimal helpers to read/write a persistent browser cookie. The value stored is a
// base64-encoded JSON blob of user preferences.
window.userPreferences = {
    get: function (name) {
        const prefix = name + "=";
        const parts = document.cookie ? document.cookie.split(";") : [];
        for (let part of parts) {
            part = part.trim();
            if (part.startsWith(prefix)) {
                return decodeURIComponent(part.substring(prefix.length));
            }
        }
        return null;
    },
    set: function (name, value) {
        // Keep preferences, including onboarding progress, across browser sessions for one year.
        document.cookie = name + "=" + encodeURIComponent(value) + "; path=/; Max-Age=31536000; SameSite=Lax";
    },
    clear: function (name) {
        document.cookie = name + "=; path=/; Max-Age=0; SameSite=Lax";
    },
    getSessionFlag: function (name) {
        try {
            return sessionStorage.getItem(name) === "true";
        } catch {
            return false;
        }
    },
    setSessionFlag: function (name) {
        try {
            sessionStorage.setItem(name, "true");
        } catch {
            // Session storage is best-effort when browser privacy settings block it.
        }
    },
    initializeAuthenticationWarning: function () {
        const warning = document.querySelector("[data-authentication-warning]");
        if (!warning || warning.dataset.initialized === "true") {
            return;
        }

        warning.dataset.initialized = "true";
        const status = document.querySelector("[data-authentication-status]");
        if (window.userPreferences.getSessionFlag("companion-authentication-warning-dismissed")) {
            warning.hidden = true;
            if (status) {
                status.hidden = false;
            }
            return;
        }

        const selectProvider = function (provider) {
            warning.querySelectorAll("[data-authentication-tab]").forEach(function (tab) {
                const selected = tab.dataset.authenticationTab === provider;
                tab.classList.toggle("active", selected);
                tab.setAttribute("aria-selected", selected ? "true" : "false");
            });
            warning.querySelectorAll("[data-authentication-panel]").forEach(function (panel) {
                panel.hidden = panel.dataset.authenticationPanel !== provider;
            });
        };

        const configuredProvider = warning.dataset.authenticationProvider || "None";
        selectProvider(["None", "GitHub", "Google", "OAuth2"].includes(configuredProvider)
            ? configuredProvider
            : "None");
    },
    setOnboardingStep: function (step) {
        window.companionAppOnboarding = { currentStep: step };
        window.dispatchEvent(new CustomEvent("companion-app:onboarding-changed", {
            detail: window.companionAppOnboarding
        }));
    },
    subscribeOnboarding: function (dotNetReference) {
        const handler = function (event) {
            const step = event.detail && event.detail.currentStep;
            if (step) {
                dotNetReference.invokeMethodAsync("OnOnboardingStepChanged", step);
            }
        };

        window.userPreferences.onboardingHandler = handler;
        window.addEventListener("companion-app:onboarding-changed", handler);
    },
    unsubscribeOnboarding: function () {
        if (window.userPreferences.onboardingHandler) {
            window.removeEventListener("companion-app:onboarding-changed", window.userPreferences.onboardingHandler);
            delete window.userPreferences.onboardingHandler;
        }
    }
};

document.addEventListener("click", function (event) {
    const tab = event.target.closest("[data-authentication-tab]");
    if (tab) {
        const warning = tab.closest("[data-authentication-warning]");
        const provider = tab.dataset.authenticationTab;
        warning.querySelectorAll("[data-authentication-tab]").forEach(function (candidate) {
            const selected = candidate === tab;
            candidate.classList.toggle("active", selected);
            candidate.setAttribute("aria-selected", selected ? "true" : "false");
        });
        warning.querySelectorAll("[data-authentication-panel]").forEach(function (panel) {
            panel.hidden = panel.dataset.authenticationPanel !== provider;
        });
        return;
    }

    const dismissButton = event.target.closest("[data-dismiss-authentication-warning]");
    if (dismissButton) {
        const warning = dismissButton.closest("[data-authentication-warning]");
        window.userPreferences.setSessionFlag("companion-authentication-warning-dismissed");
        warning.hidden = true;
        const status = document.querySelector("[data-authentication-status]");
        if (status) {
            status.hidden = false;
        }
    }

    const reviewButton = event.target.closest("[data-show-authentication-warning]");
    if (reviewButton) {
        const warning = document.querySelector("[data-authentication-warning]");
        const status = document.querySelector("[data-authentication-status]");
        warning.hidden = false;
        if (status) {
            status.hidden = true;
        }
    }
});

window.addEventListener("DOMContentLoaded", function () {
    window.userPreferences.initializeAuthenticationWarning();
});
window.userPreferences.initializeAuthenticationWarning();

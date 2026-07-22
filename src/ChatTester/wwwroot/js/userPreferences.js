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
    setOnboardingStep: function (step) {
        window.chatTesterOnboarding = { currentStep: step };
        window.dispatchEvent(new CustomEvent("chat-tester:onboarding-changed", {
            detail: window.chatTesterOnboarding
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
        window.addEventListener("chat-tester:onboarding-changed", handler);
    },
    unsubscribeOnboarding: function () {
        if (window.userPreferences.onboardingHandler) {
            window.removeEventListener("chat-tester:onboarding-changed", window.userPreferences.onboardingHandler);
            delete window.userPreferences.onboardingHandler;
        }
    }
};

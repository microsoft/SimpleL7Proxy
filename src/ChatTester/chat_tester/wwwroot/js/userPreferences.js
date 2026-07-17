// Minimal helpers to read/write a browser session cookie (no expiry => cleared when the
// browser session ends). The value stored is a base64-encoded JSON blob of user preferences.
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
        // Session cookie: no "expires"/"max-age" so it is discarded when the browser closes.
        document.cookie = name + "=" + encodeURIComponent(value) + "; path=/; SameSite=Lax";
    },
    clear: function (name) {
        document.cookie = name + "=; path=/; Max-Age=0; SameSite=Lax";
    }
};

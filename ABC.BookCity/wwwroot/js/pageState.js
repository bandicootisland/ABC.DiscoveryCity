// Simple localStorage-backed page state helpers
// Used by Blazor WASM pages to persist grid/pager state across navigation.

window.pageState = {
    get: function (key) {
        try {
            return localStorage.getItem(key);
        } catch {
            return null;
        }
    },

    set: function (key, value) {
        try {
            // Blazor already JSON-serializes objects, so if value is already a string, store as-is.
            // Otherwise stringify (for direct JS usage).
            var toStore = (typeof value === 'string') ? value : JSON.stringify(value);
            localStorage.setItem(key, toStore);
            return true;
        } catch {
            return false;
        }
    },

    remove: function (key) {
        try {
            localStorage.removeItem(key);
            return true;
        } catch {
            return false;
        }
    }
};

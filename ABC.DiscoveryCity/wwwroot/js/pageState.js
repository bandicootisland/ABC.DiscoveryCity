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
    },

    addToSearchHistory: function (query) {
        if (!query || !query.trim()) return;
        try {
            var raw = localStorage.getItem('searchHistory');
            var history = raw ? JSON.parse(raw) : [];
            history = history.filter(h => h !== query);
            history.unshift(query);
            if (history.length > 20) history = history.slice(0, 20);
            localStorage.setItem('searchHistory', JSON.stringify(history));
        } catch { }
    },

    getSearchHistory: function () {
        try {
            var raw = localStorage.getItem('searchHistory');
            return raw ? JSON.parse(raw) : [];
        } catch {
            return [];
        }
    },

    clearSearchHistory: function () {
        try {
            localStorage.removeItem('searchHistory');
            return true;
        } catch {
            return false;
        }
    }
};

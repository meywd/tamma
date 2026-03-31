// Tamma Studio — localStorage interop for Blazor WASM.
// Invoked via IJSRuntime from LocalStorageService.cs.

window.tammaLocalStorage = {
    getItem: function (key) {
        return localStorage.getItem(key);
    },
    setItem: function (key, value) {
        localStorage.setItem(key, value);
    },
    removeItem: function (key) {
        localStorage.removeItem(key);
    }
};

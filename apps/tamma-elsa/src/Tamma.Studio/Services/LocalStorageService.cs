using System.Text.Json;
using Microsoft.JSInterop;

namespace Tamma.Studio.Services;

/// <summary>
/// Blazor JS interop wrapper for browser <c>localStorage</c>.
/// Requires <c>wwwroot/js/local-storage.js</c> to be loaded in index.html.
/// </summary>
public sealed class LocalStorageService
{
    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// Gets a typed value from localStorage, deserialising from JSON.
    /// Returns <paramref name="defaultValue"/> when the key does not exist
    /// or the stored value cannot be deserialised.
    /// </summary>
    public async Task<T> GetAsync<T>(string key, T defaultValue = default!)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("tammaLocalStorage.getItem", key);
            if (json is null)
                return defaultValue;

            return JsonSerializer.Deserialize<T>(json) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Gets a raw string value from localStorage.
    /// Returns <c>null</c> when the key does not exist.
    /// </summary>
    public async Task<string?> GetStringAsync(string key)
    {
        try
        {
            return await _js.InvokeAsync<string?>("tammaLocalStorage.getItem", key);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stores a typed value in localStorage as JSON.
    /// </summary>
    public async Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await _js.InvokeVoidAsync("tammaLocalStorage.setItem", key, json);
    }

    /// <summary>
    /// Stores a raw string value in localStorage.
    /// </summary>
    public async Task SetStringAsync(string key, string value)
    {
        await _js.InvokeVoidAsync("tammaLocalStorage.setItem", key, value);
    }

    /// <summary>
    /// Removes a key from localStorage.
    /// </summary>
    public async Task RemoveAsync(string key)
    {
        await _js.InvokeVoidAsync("tammaLocalStorage.removeItem", key);
    }
}

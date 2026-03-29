namespace Tamma.Studio.Services;

/// <summary>
/// Manages user preferences, persisting them to browser localStorage.
/// Auto-loads on first access and auto-saves on every change.
/// </summary>
public sealed class UserPreferencesService
{
    private const string StorageKey = "tamma-studio-preferences";

    private readonly LocalStorageService _storage;
    private UserPreferences? _preferences;
    private bool _loaded;

    /// <summary>
    /// Raised whenever any preference value changes and has been persisted.
    /// </summary>
    public event Action? PreferencesChanged;

    public UserPreferencesService(LocalStorageService storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Returns the current preferences, loading from localStorage on first call.
    /// </summary>
    public async Task<UserPreferences> GetAsync()
    {
        if (!_loaded)
        {
            _preferences = await _storage.GetAsync<UserPreferences>(StorageKey)
                           ?? new UserPreferences();
            _loaded = true;
        }
        return _preferences!;
    }

    /// <summary>
    /// Saves the current preferences to localStorage and raises <see cref="PreferencesChanged"/>.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_preferences is not null)
        {
            await _storage.SetAsync(StorageKey, _preferences);
            PreferencesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Updates preferences via a mutator delegate, then persists immediately.
    /// </summary>
    public async Task UpdateAsync(Action<UserPreferences> mutate)
    {
        var prefs = await GetAsync();
        mutate(prefs);
        await SaveAsync();
    }
}

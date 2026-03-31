using Elsa.Studio.Contracts;
using MudBlazor;
using Tamma.Studio.Services;

namespace Tamma.Studio.Theming;

/// <summary>
/// Custom <see cref="IThemeService"/> implementation that persists dark mode
/// preference to localStorage via <see cref="UserPreferencesService"/>.
///
/// On startup it reads the saved preference and applies the correct palette.
/// When dark mode is toggled it saves the new state immediately.
/// </summary>
public sealed class TammaThemeService : IThemeService
{
    private readonly UserPreferencesService _prefs;
    private bool _isDarkMode;
    private MudTheme _currentTheme;
    private bool _initialized;

    public TammaThemeService(UserPreferencesService prefs)
    {
        _prefs = prefs;
        _currentTheme = TammaThemeProvider.Theme;
    }

    /// <inheritdoc />
    public event Action? IsDarkModeChanged;

    /// <inheritdoc />
    public event Action? CurrentThemeChanged;

    /// <inheritdoc />
    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (_isDarkMode == value)
                return;
            _isDarkMode = value;
            IsDarkModeChanged?.Invoke();
            _ = PersistDarkModeAsync(value);
        }
    }

    /// <inheritdoc />
    public MudTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            _currentTheme = value;
            CurrentThemeChanged?.Invoke();
        }
    }

    /// <summary>
    /// Loads dark mode preference from localStorage and applies it.
    /// Must be called after Blazor has rendered (JS interop is available).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var prefs = await _prefs.GetAsync();
        _isDarkMode = prefs.IsDarkMode;
        _currentTheme = TammaThemeProvider.Theme;
        IsDarkModeChanged?.Invoke();
        CurrentThemeChanged?.Invoke();
    }

    private async Task PersistDarkModeAsync(bool isDark)
    {
        await _prefs.UpdateAsync(p => p.IsDarkMode = isDark);
    }
}

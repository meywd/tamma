using MudBlazor;

namespace Tamma.Studio.Theming;

/// <summary>
/// Provides the Tamma MudBlazor theme with purple primary palette.
/// The theme is applied by registering a custom <see cref="Elsa.Studio.Contracts.IThemeService"/>
/// or by setting <c>IThemeService.CurrentTheme</c> at startup.
/// </summary>
public static class TammaThemeProvider
{
    /// <summary>Primary brand color — Tamma purple.</summary>
    public const string PrimaryColor = "#7B61FF";

    /// <summary>Secondary accent color — emerald green.</summary>
    public const string SecondaryColor = "#10b981";

    public static MudTheme Theme => new()
    {
        LayoutProperties =
        {
            DefaultBorderRadius = "4px",
        },
        PaletteLight =
        {
            Primary = PrimaryColor,
            Secondary = SecondaryColor,
            AppbarBackground = "#1a1a2e",
            AppbarText = "#ffffff",
            Background = "#fafafa",
            Surface = "#ffffff",
            DrawerBackground = "#f8fafc",
            DrawerText = "#1a1a2e",
            DrawerIcon = PrimaryColor,
        },
        PaletteDark =
        {
            Primary = "#9B85FF",
            Secondary = "#34d399",
            AppbarBackground = "#0f0f1e",
            AppbarText = "#e0e0e0",
            Background = "#0f172a",
            Surface = "#182234",
            DrawerBackground = "#0f0f1e",
            DrawerText = "#c0c0c0",
            DrawerIcon = "#9B85FF",
        },
    };
}

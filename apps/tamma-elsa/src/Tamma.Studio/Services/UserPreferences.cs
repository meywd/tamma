namespace Tamma.Studio.Services;

/// <summary>
/// POCO holding all persisted user preferences for Tamma Studio.
/// Serialised to/from localStorage as a single JSON blob.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>Whether dark mode is active.</summary>
    public bool IsDarkMode { get; set; }

    /// <summary>Flowchart designer grid size in pixels.</summary>
    public int GridSize { get; set; } = 20;

    /// <summary>Flowchart designer zoom level (0.25 – 3.0).</summary>
    public double ZoomLevel { get; set; } = 1.0;

    /// <summary>Whether the navigation sidebar is collapsed.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Activity picker display mode: "accordion" or "treeview".</summary>
    public string ActivityPickerMode { get; set; } = "accordion";

    /// <summary>Monaco / code editor font size in pixels.</summary>
    public int EditorFontSize { get; set; } = 14;
}

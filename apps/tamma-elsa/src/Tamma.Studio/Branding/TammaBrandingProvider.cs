using Elsa.Studio.Branding;
using Microsoft.AspNetCore.Components;

namespace Tamma.Studio.Branding;

/// <summary>
/// Provides Tamma branding for ELSA Studio: app name, logo, and custom branding component.
/// Inherits from <see cref="DefaultBrandingProvider"/> to reuse the default RenderFragment
/// while overriding identity properties.
/// Registered in DI as <see cref="IBrandingProvider"/> in Program.cs.
/// </summary>
public class TammaBrandingProvider : DefaultBrandingProvider
{
    /// <summary>Displayed in the browser tab and Studio header.</summary>
    public override string AppName => "Tamma Studio";

    /// <summary>Logo on light background — shown in the Studio navigation sidebar.</summary>
    public override string LogoUrl => "logo.svg";

    /// <summary>Logo on dark background — used when dark mode is active.</summary>
    public override string LogoReverseUrl => "logo-dark.svg";
}

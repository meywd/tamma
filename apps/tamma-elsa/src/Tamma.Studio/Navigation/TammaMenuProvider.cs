using Elsa.Studio.Contracts;
using Elsa.Studio.Models;

namespace Tamma.Studio.Navigation;

/// <summary>
/// Adds Tamma-specific menu items to the ELSA Studio navigation sidebar.
/// Links to filtered workflow instance views for ADL, LLM, and Mentorship workflows.
///
/// Implements <see cref="IMenuProvider"/> from Elsa.Studio.Contracts (Elsa.Studio.Core assembly).
/// Registered in DI via <c>services.AddScoped&lt;IMenuProvider, TammaMenuProvider&gt;()</c>.
/// </summary>
public class TammaMenuProvider : IMenuProvider
{
    /// <inheritdoc />
    public ValueTask<IEnumerable<MenuItem>> GetMenuItemsAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<MenuItem>
        {
            new()
            {
                Text = "ADL Dashboard",
                Href = "/workflow-instances?definitionId=adl-orchestrator",
                Icon = "Dashboard",
                GroupName = "general",
                Order = 100,
            },
            new()
            {
                Text = "LLM Diagnostics",
                Href = "/workflow-instances?definitionId=llm-call",
                Icon = "Analytics",
                GroupName = "general",
                Order = 101,
            },
            new()
            {
                Text = "Mentorship",
                Href = "/workflow-instances?definitionId=mentorship",
                Icon = "School",
                GroupName = "general",
                Order = 102,
            },
            // Wave C.3 (Story 5.6 / 1.5-37) — platform-admin alert surface.
            // Separate group so the operator sees alert tools clustered.
            new()
            {
                Text = "Alert feed",
                Href = "/admin/alerts",
                Icon = "NotificationsActive",
                GroupName = "alerts",
                Order = 200,
            },
            new()
            {
                Text = "Alert rules",
                Href = "/admin/alert-rules",
                Icon = "Rule",
                GroupName = "alerts",
                Order = 201,
            },
            new()
            {
                Text = "Alert channels",
                Href = "/admin/alert-channels",
                Icon = "Campaign",
                GroupName = "alerts",
                Order = 202,
            },
        };

        return ValueTask.FromResult<IEnumerable<MenuItem>>(items);
    }
}

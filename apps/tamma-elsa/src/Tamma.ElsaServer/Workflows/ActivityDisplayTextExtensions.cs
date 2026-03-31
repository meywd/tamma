using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Fluent helper to call SetDisplayText inline within collection initializers.
/// Returns the activity so it can be used in Activities = { WithLabel(new ..., "label") }.
/// </summary>
internal static class ActivityDisplayTextExtensions
{
    /// <summary>
    /// Sets the display text on an activity and returns it for inline use.
    /// </summary>
    internal static T WithLabel<T>(T activity, string displayText) where T : IActivity
    {
        activity.SetDisplayText(displayText);
        return activity;
    }
}

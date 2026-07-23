using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Runtime.Activities;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 — shared activity-tree walk for the assessment-family structure suites
/// (the same reflection idiom the 39-12 IssueDecomposition suite uses, extracted so the four
/// rewritten suites don't each re-declare it).
/// </summary>
internal static class StructureWalk
{
    public static List<IActivity> All(IActivity root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IActivity>();
        stack.Push(root);
        var result = new List<IActivity>();
        while (stack.Count > 0)
        {
            var a = stack.Pop();
            if (a is null || !seen.Add(a)) continue;
            result.Add(a);
            foreach (var child in Children(a)) stack.Push(child);
        }
        return result;
    }

    private static IEnumerable<IActivity> Children(IActivity activity)
    {
        var type = activity.GetType();
        var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member switch
                {
                    PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.GetValue(activity),
                    FieldInfo f => f.GetValue(activity),
                    _ => null,
                };
            }
            catch { continue; }

            if (value is IActivity child) yield return child;
            else if (value is System.Collections.IEnumerable en and not string)
                foreach (var item in en) if (item is IActivity nested) yield return nested;
        }
    }

    /// <summary>The literal WorkflowDefinitionId string, or null when it is a delegate (variable-backed).</summary>
    public static string? LiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }
}

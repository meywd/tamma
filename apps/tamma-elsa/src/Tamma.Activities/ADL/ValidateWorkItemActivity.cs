using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Validates a work item received from the ADL Orchestrator.
/// Parses the JSON, confirms the issue exists and is processable.
///
/// Outcomes:
///   - Valid: work item is good to process
///   - Invalid: work item can't be processed (error reason in output)
/// </summary>
[Activity(
    "Tamma.ADL",
    "Validate Work Item",
    "Validate the work item received from the orchestrator",
    Kind = ActivityKind.Task
)]
[FlowNode("Valid", "Invalid")]
public class ValidateWorkItemActivity : TammaOutcomeActivity
{
    public override string? EventType => "CYCLE.WORKITEM.VALIDATE";

    [Input(Description = "Work item JSON from ADL Orchestrator")]
    public Input<string> WorkItemJson { get; set; } = default!;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Output(Description = "Parsed work item type")]
    public Output<string?> WorkItemType { get; set; } = default!;

    [Output(Description = "Error message if invalid")]
    public Output<string?> ErrorMessage { get; set; } = default!;

    [JsonConstructor]
    public ValidateWorkItemActivity() { }

    public ValidateWorkItemActivity(ILogger<ValidateWorkItemActivity> logger)
    {
        Logger = logger;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var json = WorkItemJson.Get(context);
        var repo = Repository.Get(context);

        if (string.IsNullOrWhiteSpace(json))
        {
            ErrorMessage.Set(context, "Empty work item JSON");
            await context.CompleteActivityWithOutcomesAsync("Invalid");
            return;
        }

        try
        {
            var item = JsonSerializer.Deserialize<WorkItem>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (item == null)
            {
                ErrorMessage.Set(context, "Failed to parse work item JSON");
                await context.CompleteActivityWithOutcomesAsync("Invalid");
                return;
            }

            if (item.Number <= 0)
            {
                ErrorMessage.Set(context, $"Invalid issue number: {item.Number}");
                await context.CompleteActivityWithOutcomesAsync("Invalid");
                return;
            }

            WorkItemType.Set(context, item.Type);
            Logger?.LogInformation(
                "Work item validated: #{Number} [{Type}] {Title}",
                item.Number, item.Type, item.Title);
            await context.CompleteActivityWithOutcomesAsync("Valid");
        }
        catch (JsonException ex)
        {
            ErrorMessage.Set(context, $"JSON parse error: {ex.Message}");
            await context.CompleteActivityWithOutcomesAsync("Invalid");
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["workItemType"] = this.GetOutput<string?>(context, nameof(WorkItemType)),
        ["errorMessage"] = this.GetOutput<string?>(context, nameof(ErrorMessage)),
    };
}

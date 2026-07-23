namespace Tamma.Activities.Clarify.Models;

/// <summary>
/// Outcome of delivering the clarifying questions to the stakeholder. Kept after the Story
/// 39-13 migration (the deliver activity's result type); <c>ClarifyQuestionSet</c> and
/// <c>ClarificationResult</c> were retired with <c>ClarifyParsing</c> — shape knowledge now
/// lives in the typed <c>Tamma.Core.Documents.Types.Clarification</c> document.
/// </summary>
public sealed class ClarifyDeliveryResult
{
    public bool Success { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}

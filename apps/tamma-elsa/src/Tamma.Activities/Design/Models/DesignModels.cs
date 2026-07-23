namespace Tamma.Activities.Design.Models;

/// <summary>
/// Outcome of delivering the design proposal to the reviewer / issue. Kept after the Story
/// 39-13 migration (the deliver activity's result type); <c>DesignProposal</c> and
/// <c>DesignAlternative</c> were retired with <c>DesignParsing</c> — shape knowledge now lives
/// in the typed <c>Tamma.Core.Documents.Types.Design</c> document.
/// </summary>
public sealed class DesignDeliveryResult
{
    public bool Success { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}

using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Activities.SecretsRotation.Activities;

/// <summary>
/// Story 29-6 — shared state passed between rotation activities via
/// workflow variables. The workflow creates one instance and each
/// activity reads + mutates specific slots. Modelled as a POCO (not a
/// record) so Elsa can serialize it into the bookmark for resumable
/// execution.
/// </summary>
public class RotationWorkflowState
{
    /// <summary>Secret being rotated.</summary>
    public Guid SecretId { get; set; }

    /// <summary>Rotation correlation id threaded through every event + handler call.</summary>
    public string RotationCorrelationId { get; set; } = string.Empty;

    /// <summary>Operator that triggered the rotation (Guid.Empty for auto/scheduled).</summary>
    public Guid OperatorUserId { get; set; }

    /// <summary>Grace-window seconds. 0 means "use default (900s)".</summary>
    public long GraceWindowSeconds { get; set; }

    /// <summary>Freshly-generated plaintext (populated at mint-start, scrubbed at end).</summary>
    public string NewPlaintext { get; set; } = string.Empty;

    /// <summary>New version number minted by MintPendingVersionActivity.</summary>
    public int NewVersionNumber { get; set; }

    /// <summary>Previous active version number (0 if first rotation).</summary>
    public int PreviousVersionNumber { get; set; }

    /// <summary>Snapshot of the secret's rotation-relevant fields.</summary>
    public SecretRotationSnapshot? Snapshot { get; set; }

    /// <summary>Handler system key resolved from the secret's first consumer ref.</summary>
    public string HandlerSystem { get; set; } = string.Empty;

    /// <summary>Terminal result of the rotation.</summary>
    public string Result { get; set; } = "pending";

    /// <summary>Short machine-readable reason captured on failure.</summary>
    public string? Error { get; set; }

    /// <summary>Handler option overrides (stringly-typed to survive Elsa serialization).</summary>
    public Dictionary<string, string> HandlerOptions { get; set; } = new();

    /// <summary>
    /// Has ActivateVersionActivity fired? Tracked so the compensation
    /// path knows whether to call RevertActivationAsync.
    /// </summary>
    public bool Activated { get; set; }

    /// <summary>
    /// Has PushNewValueActivity succeeded? Tracked so the compensation
    /// path knows whether to call RollbackAsync on the handler.
    /// </summary>
    public bool Pushed { get; set; }
}

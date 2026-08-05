namespace Tyanor;

/// <summary>
/// Why an operation stopped — the three classes that matter, because each earns a DIFFERENT response.
///
/// <para>Collapsing these into "it failed" is the defect this type exists to prevent: an expired token and
/// a malformed template both end the run, but only one of them means the work so far is worthless. A tool
/// that hard-fails a credential error throws away twenty minutes of correct provisioning and teaches its
/// users to fear it.</para>
/// </summary>
public enum FailureClass
{
    /// <summary>The provider rejected who we are — expired, revoked, wrong, or not yet supplied.
    /// The work already done is intact. Re-authenticate and resume.</summary>
    Credentials,

    /// <summary>Throttling, a 5xx, a dropped socket, a timeout. Retry bounded; if it persists, PAUSE —
    /// still resumable, because nothing about the desired state is wrong.</summary>
    Transient,

    /// <summary>The request itself is wrong, or the provider refuses on policy: a malformed definition, a
    /// quota that needs a human, an account not verified. Retrying is dishonest — surface it.</summary>
    Hard,
}

/// <summary>
/// Why a run is paused rather than failed. A pause is a RESUMABLE stop: the run can be re-entered and will
/// reconcile from whatever is true then.
/// </summary>
/// <remarks>
/// These are open on purpose — a provider or a procedure may introduce its own reason (a DNS validation
/// still pending, a manual approval gate) — so this is a value type over a string rather than an enum
/// nobody outside the assembly can extend.
/// </remarks>
public readonly record struct PauseReason(string Value)
{
    /// <summary>The provider rejected our identity.</summary>
    public static readonly PauseReason Credentials = new("credentials");

    /// <summary>A transient provider error outlasted the retry budget.</summary>
    public static readonly PauseReason Transient = new("transient");

    /// <summary>Waiting on something outside the provider's control (DNS propagation, a human).</summary>
    public static readonly PauseReason External = new("external");

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// The outcome of one operation, procedure or run. A stop is either RESUMABLE (with a
/// <see cref="Reason"/>) or terminal — never ambiguous, because the caller has to decide whether to
/// offer the owner a Resume button.
/// </summary>
/// <param name="Ok">The operation completed as intended.</param>
/// <param name="Error">Provider or engine detail. Present iff <paramref name="Ok"/> is false.</param>
/// <param name="Reason">Set iff the stop is resumable — see <see cref="PauseReason"/>.</param>
public sealed record OperationOutcome(bool Ok, string? Error = null, PauseReason? Reason = null)
{
    /// <summary>A resumable stop can be re-entered; a terminal one needs the definition changed.</summary>
    public bool Resumable => Reason is not null;

    /// <summary>Succeeded.</summary>
    public static OperationOutcome Success() => new(true);

    /// <summary>Stopped, but re-running will pick up where this left off.</summary>
    public static OperationOutcome Paused(PauseReason reason, string? error = null) => new(false, error, reason);

    /// <summary>Stopped for a reason re-running will not fix.</summary>
    public static OperationOutcome Failed(string error) => new(false, error);

    /// <summary>
    /// Turn a classified failure into an outcome. The mapping is the doctrine in one line: only
    /// <see cref="FailureClass.Hard"/> is terminal.
    /// </summary>
    public static OperationOutcome From(FailureClass failure, string? error = null) => failure switch
    {
        FailureClass.Credentials => Paused(PauseReason.Credentials, error),
        FailureClass.Transient => Paused(PauseReason.Transient, error),
        _ => Failed(error ?? "The operation failed."),
    };
}

/// <summary>
/// A provider's ability to say what one of ITS errors means. This is the only place a provider's error
/// codes belong; the engine never inspects an exception it did not create.
/// </summary>
public interface IFailureClassifier
{
    /// <summary>
    /// Classify <paramref name="error"/>, or return <c>null</c> if this classifier does not recognise it —
    /// in which case the engine treats it as <see cref="FailureClass.Hard"/>.
    /// </summary>
    /// <remarks>
    /// Walk the whole <see cref="Exception.InnerException"/> chain. Providers routinely wrap the
    /// informative exception inside a generic one, and a classifier that only reads the outermost will
    /// call an expired token a hard failure.
    /// </remarks>
    FailureClass? Classify(Exception error);
}

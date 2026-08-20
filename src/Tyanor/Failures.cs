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
/// <para>These are open on purpose — a provider or a procedure may introduce its own reason (a DNS
/// validation still pending, a manual approval gate) — so this is a value type over a string rather than an
/// enum nobody outside the assembly can extend.</para>
/// <para><b>A driver reaches that open end by throwing <see cref="UnitPausedException"/>.</b> The three
/// below are what the ENGINE produces on its own: two from the failure classes, and
/// <see cref="External"/> when the caller cancels. Without that exception the openness was a promise the
/// surface could not keep — no driver could cause a reason of its own, which is the shape of gap this
/// library keeps finding: a capability defined by documentation and guarded by nothing.</para>
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
/// A unit is waiting on something OUTSIDE the provider, and the run should PAUSE rather than fail.
///
/// <para><b>This is how a driver reaches <see cref="PauseReason"/>'s open end.</b> That type says a provider
/// or a procedure may introduce its own reason — a DNS validation still pending, a manual approval gate — and
/// for a long time nothing could: the engine produced <see cref="PauseReason.Credentials"/> and
/// <see cref="PauseReason.Transient"/> from the three failure classes, and
/// <see cref="PauseReason.External"/> only when the caller cancelled. A capability defined by documentation
/// and guarded by nothing, which is the shape of defect this library keeps finding in itself.</para>
///
/// <para><b>It is deliberately not a fourth <see cref="FailureClass"/>.</b> The three classes each answer
/// "what should the operator do next", and they are the provider's reading of an ERROR. This is not an error
/// at all — nothing went wrong, the work so far is intact and correct, and what is needed is a person or the
/// passage of time. Adding a class would have made every classifier's switch wrong, including the ones
/// written outside this repository, to describe something no classifier is looking at.</para>
///
/// <para><b>The message is the instruction.</b> Whatever the operator has to DO goes here, because a pause
/// they cannot act on is a stop with extra steps: <c>"Add these DNS records, then resume: …"</c>.</para>
///
/// <example>
/// <code>
/// if (certificate.Status == "PENDING_VALIDATION")
///     throw new UnitPausedException(
///         new PauseReason("dns-validation"),
///         $"{context.Label}: add these records at your registrar, then resume — {Describe(records)}");
/// </code>
/// </example>
///
/// <para>The engine records the run as <see cref="RunStatus.Paused"/> with this reason, returns a resumable
/// <see cref="OperationOutcome"/>, and re-entering reconciles from whatever is true then — so a unit that
/// pauses this way needs no resume path of its own. It is never retried: a pause is not a transient error,
/// and waiting for a human five times in quick succession helps nobody.</para>
/// </summary>
/// <param name="reason">Why. <see cref="PauseReason.External"/> covers most cases; name your own when an
/// operator would act differently on it.</param>
/// <param name="message">What the operator has to do, in plain language.</param>
public sealed class UnitPausedException(PauseReason reason, string message) : Exception(message)
{
    /// <summary>Why the run paused — carried through to <see cref="RunRecord.Reason"/>.</summary>
    public PauseReason Reason { get; } = reason;
}

/// <summary>
/// The PROCEDURE or the REQUEST is wrong — not the provider, and not the infrastructure.
///
/// <para>A unit that does not say what it is, an artifact part that was never built, a cross-unit reference
/// that does not parse. Always terminal: retrying re-reads the same definition and reaches the same
/// conclusion. Nothing about the target has been touched when one of these is raised, which is the point of
/// raising it early.</para>
///
/// <para><b>Why this is a base class and not just a message.</b> A consumer showing a deployment to a person
/// needs to tell two situations apart: "you have configured this wrongly, fix it and nothing is lost" and
/// "AWS said no". They read differently, they go to different places in a UI, and only one of them is worth
/// a support conversation. Catching a base type is how that stays possible without matching on text.</para>
///
/// <para>Providers do NOT need to classify these. An <see cref="IFailureClassifier"/> returning null for one
/// is correct, and the engine's default for null is <see cref="FailureClass.Hard"/> — which is exactly what
/// a wrong definition is.</para>
/// </summary>
/// <param name="message">Plain language, naming what was expected and what was found instead.</param>
public abstract class DefinitionException(string message) : Exception(message);

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
    /// Walk the whole <see cref="Exception.InnerException"/> chain — <see cref="FailureClassifiers.Walk"/>
    /// does it for you. Providers routinely wrap the informative exception inside a generic one, and a
    /// classifier that only reads the outermost will call an expired token a hard failure.
    /// </remarks>
    FailureClass? Classify(Exception error);
}

/// <summary>
/// The two things every <see cref="IFailureClassifier"/> needs and neither should write itself: reading an
/// error by asking several classifiers in turn, and looking past the exception a provider happened to throw
/// on the outside.
/// </summary>
public static class FailureClassifiers
{
    /// <summary>
    /// The first classifier that recognises an error decides it; nulls in the list are skipped.
    /// </summary>
    /// <param name="classifiers">Asked in order. A provider's own belongs first — it knows its SDK.</param>
    /// <remarks>
    /// "Not mine" is a real answer, which is what makes chaining work at all: a classifier returning null is
    /// passing rather than voting, so the next one gets its turn and an error nobody claims still lands on
    /// <see cref="FailureClass.Hard"/> the way it should.
    /// </remarks>
    public static IFailureClassifier Chain(params IFailureClassifier?[] classifiers) =>
        new Chained([.. classifiers.Where(c => c is not null).Cast<IFailureClassifier>()]);

    /// <summary>
    /// Ask <paramref name="classify"/> about <paramref name="error"/> and about every exception nested
    /// inside it, and return the first answer that is not "not mine".
    /// </summary>
    /// <param name="error">The exception as it arrived.</param>
    /// <param name="classify">
    /// Reads ONE exception — a <c>switch</c> on type and error code. Returns null for anything it does not
    /// recognise, and is asked again about the next one in.
    /// </param>
    /// <returns>The first class any nested exception is recognised as, or null when none is.</returns>
    /// <remarks>
    /// <para><b>This is the rule <c>.claude/rules/error-classification.md</c> calls the most common way a
    /// classifier goes quietly wrong, so it is written once rather than remembered.</b> Providers wrap
    /// freely — a <c>Win32Exception</c> from <c>Process.Start</c> arrives inside an
    /// <see cref="InvalidOperationException"/>, and the AWS SDK nests its own — and a classifier reading only
    /// the outermost calls an expired token a hard failure, throwing away a deployment that was intact. Both
    /// shipped classifiers hand-wrote this loop; a third written outside this repository would have had to
    /// write it too, which is the standing test in <c>CLAUDE.md</c> for what belongs in the framework.</para>
    /// <para><b><see cref="AggregateException"/> is opened fully, not followed by one link.</b>
    /// <see cref="Exception.InnerException"/> on one gives only its FIRST inner exception, so a chain walk
    /// silently ignores every sibling — and an aggregate is exactly what a task-based provider call path adds
    /// on top. A credential failure sitting second in that list read as unrecognised, which the engine turns
    /// into <see cref="FailureClass.Hard"/>: the run ends instead of pausing, and the work already done is
    /// discarded for want of looking one element to the right.</para>
    /// <para>Siblings are visited in their own order, and depth-first, so the innermost cause of the first
    /// branch is reached before the second branch is opened — the specific exception is nearly always the
    /// nested one, and it should win over a general sibling.</para>
    /// </remarks>
    public static FailureClass? Walk(Exception error, Func<Exception, FailureClass?> classify)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(classify);

        var pending = new Stack<Exception>();
        pending.Push(error);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (classify(current) is { } known) return known;

            if (current is AggregateException aggregate)
            {
                // Pushed in reverse so they POP in the order the provider declared them. An aggregate's own
                // InnerException duplicates its first entry, so this branch is exclusive.
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                    pending.Push(aggregate.InnerExceptions[i]);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return null;    // unrecognised — the engine treats it as Hard, which is the safe default
    }

    private sealed class Chained(IReadOnlyList<IFailureClassifier> classifiers) : IFailureClassifier
    {
        public FailureClass? Classify(Exception error)
        {
            foreach (var classifier in classifiers)
                if (classifier.Classify(error) is { } known) return known;

            return null;
        }
    }
}

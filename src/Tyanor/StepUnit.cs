namespace Tyanor;

/// <summary>
/// A unit whose work happens INLINE and which owns nothing — a check, a gate, a migration, a smoke test.
/// Implement two methods instead of six.
///
/// <para><b>Why this exists.</b> <see cref="IUnitDriver"/> has six required methods because a unit that
/// deploys INFRASTRUCTURE needs all six: something to update, something to remove, a control plane to wait
/// on, resources to report. A step has none of that. It answers <i>has this already happened?</i>, and it
/// does the thing. The other four are the same four lines every time — and they were written that way in six
/// places in this repository before this class existed, including in the worked example
/// <c>docs/adoption.md</c> puts in front of somebody adopting for the first time.</para>
///
/// <para><b>The point is the cost of the FIRST one.</b> Tyanor cannot ship a provider for every service on
/// day one, so an application has to be able to bring its own — and the honest measure of whether that is
/// really supported is not "is it possible" but "how much does it cost". Four one-line stubs is not much, and
/// it is four more chances to write <c>RefreshAsync</c> returning null instead of empty, and four more
/// reasons for a step to end up as a script that runs after the procedure instead of a unit inside it.</para>
///
/// <example>
/// <code>
/// internal sealed class SmokeTest(HttpClient http) : StepUnitDriver
/// {
///     public override async Task&lt;UnitPhase&gt; PhaseAsync(UnitContext context) =>
///         await Answers(context) ? UnitPhase.Ready : UnitPhase.Missing;
///
///     public override async Task CreateAsync(UnitContext context)
///     {
///         if (!await Answers(context)) throw new SmokeTestFailed($"{context.Label}: not answering.");
///     }
/// }
/// </code>
/// </example>
///
/// <para><b>Everything is virtual, including the two the interface defaults.</b>
/// <see cref="IUnitDriver.ValidateAsync"/> and <see cref="IUnitDriver.OutputsAsync"/> are default interface
/// members, which an editor will not offer you as an <c>override</c> and which a derived class cannot reach
/// through <c>base</c>. Restating them here costs nothing and makes the whole contract discoverable from one
/// keyword — which matters most to the person writing their first unit, who is exactly who this is for.</para>
///
/// <para><b>The one pairing to get right: <see cref="PhaseAsync"/> and <see cref="RemoveAsync"/> must
/// agree.</b> The default remove does nothing, which is correct for a step that leaves nothing behind — a
/// smoke test stops reporting <see cref="UnitPhase.Ready"/> on its own when the endpoint stops answering. It
/// is WRONG for a step whose phase is a latch: if yours reports <see cref="UnitPhase.Ready"/> from something
/// a teardown does not clear, override the remove to clear it. <c>UnitDriverContract</c> catches the
/// mismatch — "After removing, it is Missing again" — which is why running it against your own unit is worth
/// the ten minutes.</para>
///
/// <para>A step that genuinely CANNOT be undone — a publish — is a different question, and deliberately
/// unbuilt: see <c>TASKS.md</c> item 4. Bring the real case rather than a hypothetical one.</para>
/// </summary>
public abstract class StepUnitDriver : IUnitDriver
{
    /// <summary>
    /// Has this already happened? The one method a step cannot avoid, and the one that buys everything the
    /// engine does — skipping what is done, attaching to what is in flight, resuming after a crash, and
    /// showing up in a plan before it runs.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>Must be READ-ONLY: it runs during a plan, and a step that acts while reporting makes the
    /// plan a lie.</remarks>
    public abstract Task<UnitPhase> PhaseAsync(UnitContext context);

    /// <summary>
    /// Do the thing. A step has no control plane to hand the work to, so this is where the work happens —
    /// and where progress should be reported, because nothing else will narrate it.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// Throw to stop the run. A <see cref="DefinitionException"/> means the configuration is wrong;
    /// a <see cref="UnitPausedException"/> means a person has to act and the run should PAUSE, resumably;
    /// anything else is classified by the target's <see cref="IFailureClassifier"/>.
    /// </remarks>
    public abstract Task CreateAsync(UnitContext context);

    /// <summary>
    /// No change, which is what a step that is already done should report — and what a resume relies on.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// Override when re-running the step CAN change something: a migration with a new script, a cache warm
    /// against a new build. Reporting a change that did not happen makes a plan claim a redeploy will do
    /// something when it will not.
    /// </remarks>
    public virtual Task<bool> UpdateAsync(UnitContext context) => Task.FromResult(false);

    /// <summary>
    /// Nothing to take away. Override when the phase is a LATCH — see the note on this class.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    public virtual Task RemoveAsync(UnitContext context) => Task.CompletedTask;

    /// <summary>
    /// Removable, because most steps leave nothing behind. Override to <c>false</c> for one that is
    /// IRREVERSIBLE — a publish, an audit record, a sent notification.
    /// </summary>
    /// <param name="context">The unit and the request.</param>
    /// <remarks>
    /// This is the honest alternative to the two bad options a publish used to have: a remove that returns
    /// quietly, letting a teardown claim success over a version that is still out there, or one that throws
    /// and fails a teardown with nothing wrong with it. Say <c>false</c> and the plan reports it as RETAINED
    /// before anything runs. See <see cref="IUnitDriver.IsRemovable"/>.
    /// </remarks>
    public virtual bool IsRemovable(UnitContext context) => true;

    /// <summary>
    /// Nothing to wait for: the work finished inside <see cref="CreateAsync"/>, because there is no control
    /// plane that could still be converging after this process goes away.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    public virtual Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;

    /// <summary>
    /// Owns nothing, which is a FACT about a step rather than a gap — there is no resource for a teardown to
    /// remove or for a plan to count.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>Empty, never null: absent is an answer, and a plan of an undeployed procedure is the plan
    /// people run first.</remarks>
    public virtual Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyList<ResourceState>>([]);

    /// <summary>
    /// Nothing wrong with the configuration. Override to check yours, and use <see cref="UnitProblems"/> so
    /// every problem is REPORTED rather than the first one thrown.
    /// </summary>
    /// <param name="context">The unit and the request.</param>
    /// <remarks>Make no network calls: the value of this pass is that it works before an account exists.</remarks>
    public virtual Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Produces nothing. Override to expose a URL or a generated name, read from the target rather than from
    /// anything remembered.
    /// </summary>
    /// <param name="context">The unit and the request.</param>
    public virtual Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}

namespace Tyanor;

/// <summary>
/// Everything a driver is given about the one unit it is working on.
///
/// <para><b>Why a context and not four parameters.</b> The driver contract has already grown once — progress
/// reporting reached only <see cref="IUnitDriver.AwaitSettledAsync"/>, which is fine for a provider whose
/// work happens in a control plane it polls, and useless for one whose work happens in
/// <see cref="IUnitDriver.CreateAsync"/> because there is no control plane to hand it to. Copying a large
/// directory and waiting out a stack deletion both reported nothing.</para>
///
/// <para>Every such addition is a breaking change to every implementer, including the ones written outside
/// this repository that D15 says are first-class. Passing one record makes the next one additive. That is
/// the entire argument, and it is worth making before 1.0 rather than after.</para>
/// </summary>
/// <param name="Unit">Which unit.</param>
/// <param name="Request">What is being deployed, and where.</param>
/// <param name="Report">
/// Progress, scaled to THIS unit: 0–100 through the unit's own work, or -1 when there is no honest
/// fraction. The engine rescales into the run — see <see cref="ProgressReport.Percent"/>.
/// </param>
/// <param name="Cancellation">
/// Cancelling leaves the run LIVE on purpose: whatever the provider started is still converging out there,
/// and marking it failed would hide work that is genuinely in flight.
/// </param>
public sealed record UnitContext(
    ProcedureUnit Unit,
    DeploymentRequest Request,
    Action<ProgressReport> Report,
    CancellationToken Cancellation)
{
    /// <summary>
    /// A context with progress going nowhere and nothing to cancel — for calling a driver DIRECTLY, which
    /// tests and tooling do and the engine never does.
    /// </summary>
    /// <param name="unit">Which unit.</param>
    /// <param name="request">What is being deployed, and where.</param>
    public UnitContext(ProcedureUnit unit, DeploymentRequest request)
        : this(unit, request, _ => { }, CancellationToken.None) { }

    /// <summary>The unit's name — its stable identity, and the resume key.</summary>
    public string Name => Unit.Name;

    /// <summary>What to call the unit in front of a person.</summary>
    public string Label => Unit.Label;

    /// <summary>
    /// A setting for THIS unit, falling back to the procedure-wide one. Shorthand for
    /// <see cref="DeploymentRequest.Option(string, string)"/>, which a driver would otherwise write with the
    /// unit's name threaded through every call.
    /// </summary>
    /// <param name="key">The setting.</param>
    public string? Option(string key) => Request.Option(Unit.Name, key);

    /// <summary>
    /// A setting for THIS unit alone, with no shared fallback — see
    /// <see cref="DeploymentRequest.OwnOption"/> for when that is the one you want.
    /// </summary>
    /// <param name="key">The setting. One that IS the unit's identity: its path, its bucket, its port.</param>
    public string? OwnOption(string key) => Request.OwnOption(Unit.Name, key);

    /// <summary>A group of settings for this unit — see <see cref="DeploymentRequest.OptionSet"/>.</summary>
    /// <param name="prefix">The group, without a trailing dot.</param>
    public IReadOnlyDictionary<string, string> Options(string prefix) => Request.OptionSet(Unit.Name, prefix);

    /// <summary>The artifact being deployed.</summary>
    public DeploymentArtifact Artifact => Request.Artifact;

    /// <summary>Say something to whoever is watching.</summary>
    /// <param name="message">Plain language, for a person. Not an exception message, not a status code.</param>
    /// <param name="percent">0–100 through THIS unit, or -1 when there is no honest fraction.</param>
    /// <param name="status">Tone.</param>
    public void Progress(string message, int percent = -1, ProgressStatus status = ProgressStatus.Info) =>
        Report(new ProgressReport(Unit.Name, message, percent, status));

    /// <summary>Throw if the caller has cancelled.</summary>
    public void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();
}

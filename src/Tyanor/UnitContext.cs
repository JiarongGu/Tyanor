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

    /// <summary>
    /// This unit's ADDRESS, refusing one written procedure-wide — see
    /// <see cref="DeploymentRequest.Address"/>, which is where the reasoning is.
    /// </summary>
    /// <param name="key">The setting: its path, its bucket, its port.</param>
    /// <exception cref="OptionException">It was written unscoped, where it applies to every unit at once.</exception>
    public string? Address(string key) => Request.Address(Unit.Name, key);

    /// <summary>A group of settings for this unit — see <see cref="DeploymentRequest.OptionSet"/>.</summary>
    /// <param name="prefix">The group, without a trailing dot.</param>
    public IReadOnlyDictionary<string, string> Options(string prefix) => Request.OptionSet(Unit.Name, prefix);

    /// <summary>The artifact being deployed.</summary>
    public DeploymentArtifact Artifact => Request.Artifact;

    /// <summary>
    /// The artifact part this unit's <paramref name="option"/> names — the whole of "which part of the
    /// build is this unit made of?", in one call.
    /// </summary>
    /// <param name="option">The setting naming the part: <c>"source"</c>, <c>"template"</c>, <c>"assets"</c>.</param>
    /// <param name="expect">What the part has to be on disk, checked for the same reason
    /// <see cref="DeploymentArtifact.RequirePart"/> checks it.</param>
    /// <exception cref="ArtifactException">
    /// The option is not set, the artifact does not carry what it names, or that part is not what
    /// <paramref name="expect"/> says. Always terminal, and always raised before anything is touched.
    /// </exception>
    /// <remarks>
    /// <para><b>Here because it was written three times.</b> Every unit kind in both shipped providers
    /// reached the same two steps — read an option, then <see cref="DeploymentArtifact.RequirePart"/> — and
    /// each wrote its own sentence for the first one, so an operator who forgot <c>source</c> was told three
    /// different things depending on where they deployed. That is the defect
    /// <see cref="DeploymentArtifact.RequirePart"/> was extracted to fix, one level down; this is the rest of
    /// it. A fourth provider written outside this repository would have written a fourth sentence.</para>
    /// <para>It throws <see cref="ArtifactException"/> rather than a provider's own configuration type
    /// deliberately: an unset part option is not a fact about the provider, and a consumer telling
    /// "you configured this wrongly" from "the cloud said no" catches <see cref="DefinitionException"/>,
    /// which this is. <see cref="UnitProblems.Check"/> therefore collects it, so
    /// <see cref="IUnitDriver.ValidateAsync"/> reports it offline instead of throwing.</para>
    /// </remarks>
    public string RequirePart(string option, ArtifactPart expect = ArtifactPart.Any)
    {
        var name = Option(option)
            ?? throw new ArtifactException(
                $"Unit '{Name}' names no '{option}', so nothing says which part of the artifact it is made " +
                $"of. Set it to one of: {Artifact.Describe()}.");

        return Artifact.RequirePart(name, expect);
    }

    /// <summary>Say something to whoever is watching.</summary>
    /// <param name="message">Plain language, for a person. Not an exception message, not a status code.</param>
    /// <param name="percent">0–100 through THIS unit, or -1 when there is no honest fraction.</param>
    /// <param name="status">Tone.</param>
    public void Progress(string message, int percent = -1, ProgressStatus status = ProgressStatus.Info) =>
        Report(new ProgressReport(Unit.Name, message, percent, status));

    /// <summary>Throw if the caller has cancelled.</summary>
    public void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();
}

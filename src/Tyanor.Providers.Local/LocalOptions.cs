namespace Tyanor.Providers.Local;

/// <summary>
/// The settings this provider reads out of <see cref="DeploymentRequest.Options"/>, named in one place so
/// they can be documented once and misspelled never.
///
/// <para>Every one of these is read with <see cref="DeploymentRequest.Option(string, string)"/>, so it can
/// be written per unit (<c>"service.command"</c>) or once for the whole procedure (<c>"command"</c>). The
/// per-unit form is the normal one here: unlike a cloud provider, where every unit is the same kind of
/// thing, a machine deployment is a directory AND a process AND they are configured differently.</para>
///
/// <para>The exception is <see cref="Path"/>, which is the unit's ADDRESS and is read per unit only. A
/// shared address is a collision rather than a default — see <see cref="DeploymentRequest.OwnOption"/>.</para>
/// </summary>
public static class LocalOptions
{
    /// <summary>
    /// What this unit IS — <see cref="DirectoryKind"/> or <see cref="ProcessKind"/>. Required per unit,
    /// with no default: guessing would deploy something the operator did not describe.
    /// </summary>
    public const string Kind = "kind";

    /// <summary>A tree of files on this machine, materialized from a part of the artifact.</summary>
    public const string DirectoryKind = "directory";

    /// <summary>A long-lived process, started here and supervised by its pid.</summary>
    public const string ProcessKind = "process";

    /// <summary>
    /// Where the unit lives on disk. Defaults to <c>{root}/{prefix}/{unit}</c> — the prefix is what lets
    /// one machine host two deployments of the same procedure without them overwriting each other.
    ///
    /// <para>The files themselves land one level down, in <c>{path}/releases/{fingerprint}</c>, so a new
    /// build is never written over the one a process is running. This path is the stable NAME of the
    /// deployment; the release under it is the build.</para>
    ///
    /// <para><b>Per unit only.</b> Unlike every other setting here, this one does NOT fall back to an
    /// unscoped <c>"path"</c>: a procedure-wide directory would put every unit in the same folder, where the
    /// second to deploy prunes the first's releases and removing either removes both. Write
    /// <c>"runtime.path"</c>, never <c>"path"</c>.</para>
    /// </summary>
    public const string Path = "path";

    /// <summary>Which named part of <see cref="DeploymentArtifact"/> a directory unit is copied from.</summary>
    public const string Source = "source";

    /// <summary>The executable to run. Required for a process unit.</summary>
    public const string Command = "command";

    /// <summary>Arguments, as one command line.</summary>
    public const string Arguments = "args";

    /// <summary>Working directory for the process. Defaults to the unit named by <see cref="Watch"/>.</summary>
    public const string WorkingDirectory = "workDir";

    /// <summary>
    /// The DIRECTORY unit whose contents this process runs. Naming it makes the ordering dependency
    /// visible in configuration rather than implied: when that directory's content changes, this unit's
    /// fingerprint changes, and the plan says the service will be restarted.
    /// </summary>
    public const string Watch = "watch";

    /// <summary>
    /// A TCP port on loopback that means "this server is up". Without one, Tyanor can only report that the
    /// process is ALIVE — which it will say honestly rather than call it healthy.
    /// </summary>
    public const string HealthPort = "health.port";

    /// <summary>
    /// Seconds a process is allowed to be alive-but-not-yet-healthy before it stops being
    /// <see cref="UnitPhase.Converging"/> and becomes <see cref="UnitPhase.Broken"/>. Default 60.
    /// </summary>
    public const string HealthSeconds = "health.seconds";

    /// <summary>The default of <see cref="HealthSeconds"/>.</summary>
    public const int DefaultHealthSeconds = 60;
}

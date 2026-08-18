using System.Text.Json;

namespace Tyanor.Providers.Local;

/// <summary>Where this provider puts things on the machine.</summary>
internal static class LocalPaths
{
    /// <summary>
    /// A unit's own directory: <c>{root}/{prefix}/{unit}</c> unless the request says otherwise. The prefix
    /// is what lets one machine host two deployments of the same procedure without collision — the same
    /// job it does in an account name elsewhere.
    /// </summary>
    /// <remarks>
    /// The override is read per unit and NEVER falls back to a procedure-wide <c>"path"</c>. A shared one
    /// cannot mean what the fallback convention implies: it would put every directory unit in the same
    /// folder, so the second to deploy prunes the first's releases and removing either removes both. This
    /// path is the unit's address, and an address has to be its own — see
    /// <see cref="DeploymentRequest.OwnOption"/>.
    /// </remarks>
    public static string Unit(string root, DeploymentRequest request, string unit) =>
        request.OwnOption(unit, LocalOptions.Path) ?? Path.Combine(root, request.Prefix, unit);

    /// <summary>
    /// This provider's own bookkeeping for a deployment — pid files, and nothing else. Kept OUTSIDE the
    /// unit directories so that removing a unit removes exactly what was deployed and none of ours.
    /// </summary>
    public static string Bookkeeping(string root, DeploymentRequest request) =>
        Path.Combine(root, request.Prefix, ".tyanor");

    /// <summary>The folder holding one directory per build, inside a unit's directory.</summary>
    public const string Releases = "releases";

    /// <summary>Where a directory unit records what it put there.</summary>
    public static string Marker(string root, DeploymentRequest request, string unit) =>
        Path.Combine(Unit(root, request, unit), UnitMarker.FileName);

    /// <summary>
    /// The build currently in service for a unit, or null when nothing usable is recorded. This is the
    /// path a process unit actually runs out of — the unit's own directory is the stable NAME of the
    /// deployment, not the files.
    /// </summary>
    public static string? CurrentRelease(string root, DeploymentRequest request, string unit)
    {
        var marker = Records.Read<UnitMarker>(Marker(root, request, unit));
        return marker is null ? null : Path.Combine(Unit(root, request, unit), Releases, marker.Release);
    }

    // Overloads for the unit currently being worked on — the ordinary case, and the one that would
    // otherwise thread `request` and `unit.Name` through every call.
    public static string Unit(string root, UnitContext context) => Unit(root, context.Request, context.Name);

    public static string Bookkeeping(string root, UnitContext context) => Bookkeeping(root, context.Request);

    public static string Marker(string root, UnitContext context) => Marker(root, context.Request, context.Name);

    public static string? CurrentRelease(string root, UnitContext context) =>
        CurrentRelease(root, context.Request, context.Name);
}

/// <summary>
/// The file a directory unit leaves behind saying what it put there — the provider's own status record.
///
/// <para><b>This is not the state file D1 refused, and the difference is worth being exact about.</b> It
/// lives INSIDE the deployment, is written by the provider, and describes only what is on this disk. A
/// cloud provider gets this for free — CloudFormation remembers a stack's status server-side — but a
/// machine remembers nothing, so a provider with no server has to write down what it did or it can never
/// answer <see cref="IUnitDriver.PhaseAsync"/> again. Lose it and the unit reads as
/// <see cref="UnitPhase.Broken"/> and is remade, which is exactly what "a partial copy" deserves.</para>
///
/// <para>It is also the atomic switch between builds: it is written after a release is fully copied and
/// moved into place, so a reader sees the old release or the new one and never a half-finished either.</para>
/// </summary>
/// <param name="Unit">Which unit wrote it, so a misconfigured path is visible rather than silent.</param>
/// <param name="Source">Fingerprint of the artifact part this was copied FROM — "is there a new build?".</param>
/// <param name="Content">Fingerprint of what was written — "has anyone touched it since?".</param>
/// <param name="Release">Which folder under <c>releases/</c> is in service. The pointer a symlink would be
/// if creating one did not need privileges the operator may not have.</param>
/// <param name="RecordedAt">When the copy finished.</param>
internal sealed record UnitMarker(
    string Unit, string Source, string Content, string Release, DateTimeOffset RecordedAt)
{
    /// <summary>The marker's file name, inside the unit's directory.</summary>
    public const string FileName = ".tyanor-unit.json";
}

/// <summary>
/// What a process unit remembers about the process it started.
/// </summary>
/// <param name="Pid">The OS process id.</param>
/// <param name="StartedAt">
/// The process's OWN start time as the OS reports it, never our clock. This is the half of the identity
/// that makes the record safe: pids are reused, and a deployment tool that kills whatever now holds a
/// remembered pid is a deployment tool that kills something else's database.
/// </param>
/// <param name="Command">What was launched, for the operator reading a pid file by hand.</param>
/// <param name="Fingerprint">Command + arguments + the content it serves. Different means "restart it".</param>
internal sealed record ProcessRecord(int Pid, DateTimeOffset StartedAt, string Command, string Fingerprint);

/// <summary>Reads and writes the two small records above, atomically.</summary>
internal static class Records
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Read a record, or null when it is absent or unreadable.</summary>
    /// <remarks>
    /// Unreadable is deliberately the SAME answer as absent here, and the opposite of what
    /// <c>FileStateStore</c> does with a corrupt state file. The difference is what is at stake: a corrupt
    /// marker costs one unit a needless recreate, whereas a corrupt state file loses the record of what
    /// Tyanor owns across the whole deployment. Cheap to redo, so redo it; expensive to lose, so refuse.
    /// </remarks>
    public static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Write a record, creating its directory. Written to a temp file and moved into place, so a
    /// reader never sees half of one.</summary>
    public static void Write<T>(string path, T record)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Delete a record, tolerating one that is already gone.</summary>
    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

namespace Tyanor.Engine.State;

/// <summary>
/// The <c>json</c> backend: a file on this machine, which is what ships and what a single operator needs.
///
/// <code>
/// json:/var/lib/myapp/state.json
/// json:C:\ProgramData\myapp\state.json
/// </code>
///
/// <para>Registered by default, because it is the one backend that can be: it needs no package, no server and
/// no decision on day one. Everything else — SQLite, Postgres, S3 — is a package a consumer references, or a
/// backend they write themselves and later upstream (<c>docs/DECISIONS.md</c> D20).</para>
///
/// <para><b>What it does not do, stated rather than left to be discovered:</b> no conditional writes. Two
/// machines saving at the same instant will have one silently overwrite the other. Acceptable for a single
/// operator or a single pipeline; not acceptable for a team, which is what a remote backend is for and why
/// <see cref="DeploymentState.Serial"/> exists for one that can check it.</para>
/// </summary>
public sealed class JsonStorageBackend : IStorageBackend
{
    /// <summary>The kind this backend answers to.</summary>
    public const string JsonKind = "json";

    /// <inheritdoc/>
    public string Kind => JsonKind;

    /// <inheritdoc/>
    public IStateStore OpenState(StorageConnection connection) => new FileStateStore(Path(connection));

    /// <inheritdoc/>
    public IRunHistory OpenHistory(StorageConnection connection) => new FileRunHistory(Path(connection));

    /// <remarks>
    /// The target is a path and nothing else. A rooted one is used as given; a relative one resolves against
    /// the application's base directory rather than the working directory, because a deployment tool whose
    /// state moves when someone runs it from a different folder is a deployment tool that loses state.
    /// </remarks>
    private static string Path(StorageConnection connection) =>
        System.IO.Path.IsPathRooted(connection.Target)
            ? connection.Target
            : System.IO.Path.Combine(AppContext.BaseDirectory, connection.Target);
}

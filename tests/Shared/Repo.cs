using System.Reflection;

/// <summary>
/// Where this repository is on disk, for the tests that check a FILE rather than a behaviour — the API
/// baselines, and the documentation whose tables have to agree with the code.
/// </summary>
/// <remarks>
/// Second caller, so it moved here rather than being copied: <c>tests/Shared</c> is compiled into every
/// test project precisely so a helper is written once (<c>CLAUDE.md</c> §3).
/// </remarks>
public static class Repo
{
    /// <summary>
    /// The repository root, stamped in by <c>tests/Directory.Build.props</c> at build time.
    ///
    /// <para>Not discovered by walking up from the output directory, which is the usual trick and breaks the
    /// moment anything runs the assembly from elsewhere.</para>
    /// </summary>
    public static string Root =>
        typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value
        ?? throw new InvalidOperationException(
            "No RepoRoot in assembly metadata — tests/Directory.Build.props is meant to stamp it.");

    /// <summary>A path inside the repository.</summary>
    /// <param name="parts">Path segments below the root.</param>
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);
}

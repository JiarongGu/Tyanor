namespace Tyanor.Providers.Local;

/// <summary>
/// Deploys to THIS machine: a directory materialized from an artifact, a process started from it, a health
/// check on its port.
///
/// <code>
/// var procedure = new Procedure("server",
/// [
///     new ProcedureUnit("runtime", "Application files"),
///     new ProcedureUnit("service", "Server"),
/// ]);
///
/// var request = new DeploymentRequest("acme",
///     new DeploymentArtifact(new Dictionary&lt;string, string&gt; { ["app"] = publishOutput }),
///     new Dictionary&lt;string, string&gt;
///     {
///         ["runtime.kind"] = "directory",  ["runtime.source"] = "app",
///         ["service.kind"] = "process",    ["service.command"] = "dotnet",
///         ["service.args"] = "Server.dll", ["service.watch"] = "runtime",
///         ["service.health.port"] = "8080",
///     });
///
/// var runner = new ProcedureRunner(new LocalTarget("/srv"), history, state);
/// await runner.ApplyAsync(procedure, request);
/// </code>
///
/// <para><b>Why this provider exists beyond being useful.</b> It has no control plane. Nothing on a
/// machine keeps converging after the process that started the work goes away, nothing tracks which files
/// belong to which deployment, and nothing can be asked "what did you create?". Everything the engine
/// takes for granted when it talks to a cloud has to be built here out of a pid and a marker file — which
/// is why it is the honest test of whether the abstraction was an abstraction or a description of AWS.</para>
/// </summary>
/// <param name="root">
/// Where deployments live on this machine. Each one gets <c>{root}/{prefix}</c>, so one root can host
/// several deployments of the same procedure.
/// </param>
/// <param name="custom">
/// Units this application brings of its own — a step that is nobody's vendor's business, registered
/// alongside <c>directory</c> and <c>process</c> and reconciled exactly like them. See
/// <see cref="CustomUnits"/>.
/// </param>
public sealed class LocalTarget(string root, CustomUnits? custom = null) : IDeploymentTarget
{
    /// <inheritdoc/>
    public string Id => "local";

    /// <summary>Where deployments live on this machine.</summary>
    public string Root { get; } = root;

    /// <inheritdoc/>
    public IUnitDriver Driver { get; } = new LocalUnitDriver(root, custom);

    /// <inheritdoc/>
    public IFailureClassifier Classifier { get; } =
        FailureClassifiers.Chain(new LocalFailureClassifier(), custom?.Classifier);

    /// <summary>
    /// Who this machine thinks we are, and whether we may actually write here.
    ///
    /// <para><b>The credentials argument is ignored, and that is the point.</b> This target authenticates
    /// as whoever is running the process — pass <c>null</c>. Anything supplied is accepted and unused
    /// rather than rejected, because a caller that configures several targets uniformly should not have to
    /// special-case this one.</para>
    ///
    /// <para>The check is still a REAL one, as the contract requires: it writes a file to the root and
    /// deletes it. "Can I write where you are about to deploy?" is the local form of "are these keys
    /// valid" — the same question, asked of the thing that will refuse later if it is going to. And
    /// reporting the machine and the user is the local form of showing the account: deploying a server to
    /// the wrong host is the same mistake as deploying a stack to the wrong account.</para>
    /// </summary>
    /// <param name="credentials">Ignored. This target has an ambient identity.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct)
    {
        var probe = Path.Combine(Root, $".tyanor-write-check-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Root);
            await File.WriteAllTextAsync(probe, Id, ct);
            File.Delete(probe);
            return new TargetIdentity(true, Environment.MachineName, Environment.UserName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return new TargetIdentity(false, Environment.MachineName, Environment.UserName,
                $"Cannot write to '{Root}' as {Environment.UserName}: {e.Message}");
        }
    }

    /// <summary>
    /// Remove this provider's own bookkeeping for the deployment, and the deployment's folder with it — but
    /// only if nothing else is in there.
    /// </summary>
    /// <param name="context">The procedure and deployment that were just torn down.</param>
    /// <remarks>
    /// <para><b>The same gap the AWS provider had, on a machine instead of in an account.</b> This provider
    /// keeps pid files under <c>{root}/{prefix}/.tyanor</c>, deliberately outside the unit directories so
    /// that removing a unit removes exactly what was deployed and none of ours — which left nobody able to
    /// remove ours. A destroyed deployment left <c>{root}/{prefix}</c> and a <c>.tyanor</c> folder standing
    /// for ever. See <c>docs/DECISIONS.md</c> D33.</para>
    /// <para><b>Only if empty, and that is not caution — it is the correct answer.</b> A unit whose
    /// <c>path</c> option puts it somewhere else entirely is not swept by this, and a directory unit that
    /// was RETAINED is still there. Deleting the prefix folder recursively would take away files this
    /// teardown deliberately did not remove, which is the opposite of what a sweep is for. Anything left
    /// means something is still deployed, so the folder stays and says so.</para>
    /// <para>Everything here tolerates absence, because a teardown is re-runnable and the second one finds
    /// the first one's work done.</para>
    /// </remarks>
    public Task SweepAsync(SweepContext context)
    {
        var deployment = LocalPaths.Deployment(Root, context.Request);
        var bookkeeping = LocalPaths.Bookkeeping(Root, context.Request);

        // The pid files are already gone — a process unit deletes its own on removal — so what is left is
        // the folder that held them. Recursive is right HERE and nowhere above it: everything under
        // `.tyanor` is this provider's, and nothing of the operator's is.
        if (Directory.Exists(bookkeeping)) Directory.Delete(bookkeeping, recursive: true);

        if (Directory.Exists(deployment) && Directory.EnumerateFileSystemEntries(deployment).Any() is false)
        {
            context.Progress($"Removing the empty deployment folder {deployment}…");
            Directory.Delete(deployment);
        }

        return Task.CompletedTask;
    }
}

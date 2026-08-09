namespace Tyanor.Providers.Local;

/// <summary>
/// A unit that IS a tree of files on this machine, materialized from one named part of the artifact.
///
/// <para><b>A build is never written over the one that is running.</b> Each build lands in its own
/// directory under <c>{path}/releases/{fingerprint}</c> and the marker records which is current. This is
/// not tidiness: a process holds its working directory and the assemblies it loaded, so replacing those
/// files in place fails — on Windows outright, and everywhere else eventually. Writing beside the running
/// release and letting the process unit restart into it is what lets a redeploy work at all.</para>
///
/// <para>It is also what keeps <c>units-not-graphs.md</c> honest. "Replace the files, but stop the server
/// first, then start it again" reads like a case ordering cannot express — it wants the service both
/// after and before the runtime. It does not: changing the OPERATION so it never conflicts dissolves the
/// ordering problem entirely, and the list stays a list. See <c>docs/DECISIONS.md</c> D13.</para>
///
/// <para><b>The unit owns its directory.</b> Applying prunes releases it no longer needs and removing
/// deletes the lot — which makes it the wrong place to keep anything the application writes. Logs and
/// data belong outside it.</para>
/// </summary>
/// <param name="root">The machine's deployment root.</param>
internal sealed class DirectoryUnit(string root) : IUnitDriver
{
    /// <summary>
    /// Missing · Broken · Ready. Never <see cref="UnitPhase.Converging"/>, and that absence is honest
    /// rather than an omission: a copy is converged only by the process doing it, so nothing keeps working
    /// after that process dies. A cloud unit can be left mid-flight and attached to later precisely
    /// BECAUSE a server elsewhere is still doing the work; a filesystem has no such server. What an
    /// interrupted copy leaves behind is not work in progress — it is wreckage, and it reads as
    /// <see cref="UnitPhase.Broken"/> so the engine remakes it.
    /// </summary>
    public Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        var path = LocalPaths.Unit(root, context);
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            return Task.FromResult(UnitPhase.Missing);

        // Files but no usable marker: a FIRST copy was interrupted, or someone put this here by hand.
        // Either way what is on disk is not something we can reason about. The marker is written last and
        // moved into place atomically, so this means what it says.
        var release = LocalPaths.CurrentRelease(root, context);
        return Task.FromResult(release is null || !Directory.Exists(release) ? UnitPhase.Broken : UnitPhase.Ready);
    }

    /// <inheritdoc/>
    public Task CreateAsync(UnitContext context)
    {
        Materialize(context);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Materialize when the artifact has moved on, or when the release that is current is no longer what
    /// we wrote. Otherwise report no change — which on a resume is the ordinary answer, and is a success.
    /// </summary>
    public Task<bool> UpdateAsync(UnitContext context)
    {
        var marker = Records.Read<UnitMarker>(LocalPaths.Marker(root, context));
        var release = LocalPaths.CurrentRelease(root, context);

        // Two questions, and both have to be no. "Is there a new build?" is the one people expect; "has
        // anyone edited what I deployed?" is the one that makes the tool worth trusting, because a
        // hand-patched server that survives every redeploy is how a machine drifts away from its recipe.
        if (marker is not null
            && release is not null
            && marker.Source == Fingerprints.OfDirectory(Source(context))
            && marker.Content == Fingerprints.OfDirectory(release))
            return Task.FromResult(false);

        Materialize(context);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(UnitContext context)
    {
        var path = LocalPaths.Unit(root, context);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Returns at once — see <see cref="PhaseAsync"/> for why there is nothing to wait for.</summary>
    public Task AwaitSettledAsync(UnitContext context)
        => Task.CompletedTask;

    /// <summary>
    /// One resource: the unit's directory, identified by the path that survives a new build, and
    /// fingerprinted by what is ACTUALLY in the current release right now — not by what the marker says
    /// should be. That difference is the whole point of a refresh, and it is what turns "someone edited
    /// the deployed files" into a number on a plan.
    /// </summary>
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
    {
        var release = LocalPaths.CurrentRelease(root, context);
        var content = release is null ? null : Fingerprints.OfDirectory(release);

        return Task.FromResult<IReadOnlyList<ResourceState>>(content is null
            ? []
            : [new ResourceState(LocalPaths.Unit(root, context), "local/directory", content)]);
    }

    /// <summary>
    /// Resolve exactly what a create would resolve, and report what fails instead of throwing it.
    /// </summary>
    /// <remarks>
    /// The checks are not written twice: this runs the same <see cref="Source"/> the apply runs and collects
    /// the refusal. Two copies of a rule is two rules, and they diverge the first time one is edited.
    /// </remarks>
    public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context)
    {
        try
        {
            Source(context);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        catch (DefinitionException e)
        {
            return Task.FromResult<IReadOnlyList<string>>([e.Message]);
        }
    }

    /// <summary>Where the files ended up, so a consumer can point something at them.</summary>
    public Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context)
    {
        var release = LocalPaths.CurrentRelease(root, context);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (release is not null && Directory.Exists(release)) outputs[$"{context.Name}.path"] = release;

        return Task.FromResult<IReadOnlyDictionary<string, string>>(outputs);
    }

    private void Materialize(UnitContext context)
    {
        var source = Source(context);
        var build = Fingerprints.OfDirectory(source)!;
        var path = LocalPaths.Unit(root, context);
        var release = Path.Combine(path, LocalPaths.Releases, build);

        // Copy first, THEN move the marker. Until the last line the marker still names the previous
        // release, which is untouched — so an interruption anywhere in the copy leaves the version that
        // was already serving still serving, and correctly described. The half-written new release is
        // garbage the next deployment prunes.
        //
        // On a FIRST deployment there is no previous marker, so the same interruption leaves files with no
        // marker beside them, which reads as Broken and is remade. Both are what should happen.
        Sync(source, release, context);
        Records.Write(LocalPaths.Marker(root, context),
            new UnitMarker(context.Name, build, Fingerprints.OfDirectory(release) ?? "", build, DateTimeOffset.UtcNow));

        Prune(path, keep: build);
    }

    /// <summary>
    /// Make <paramref name="destination"/> match <paramref name="source"/> file by file, rather than
    /// deleting it and copying afresh.
    /// </summary>
    /// <remarks>
    /// The difference matters when repairing a release that is currently RUNNING: a delete-then-copy has
    /// to remove the directory a process is sitting in and cannot, whereas overwriting the individual
    /// files it does not hold open succeeds. What is genuinely locked still throws — and a sharing
    /// violation classifies as transient, so the run pauses and the operator can stop the service and
    /// resume, which is the honest answer rather than a silent partial repair.
    /// </remarks>
    private static void Sync(string source, string destination, UnitContext context)
    {
        Directory.CreateDirectory(destination);

        // Case sensitivity has to follow the filesystem, in both directions. On Windows, overwriting
        // `App.dll` with a source file named `app.dll` leaves the name on disk as `App.dll`, so an ordinal
        // comparison would decide the file we just wrote was stale and delete it. Everywhere else the two
        // names are genuinely different files, and ignoring case would delete one of them.
        var wanted = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Listed rather than streamed, because a copy that can say "412 of 900" is the difference between a
        // deployment that looks slow and one that looks stuck. This is work the ENGINE cannot narrate — it
        // happens inside a create, where a provider with a control plane would have nothing to do.
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var copied = 0;

        foreach (var file in files)
        {
            context.ThrowIfCancelled();
            var relative = Path.GetRelativePath(source, file);
            wanted.Add(relative);

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            // Percent is through THIS unit; the engine rescales it into the run.
            copied++;
            if (copied % 25 == 0 || copied == files.Count)
                context.Progress($"{context.Label}: copied {copied} of {files.Count} files…",
                    (int)(100.0 * copied / files.Count));
        }

        foreach (var stale in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                     .Where(f => !wanted.Contains(Path.GetRelativePath(destination, f))))
            File.Delete(stale);
    }

    /// <summary>
    /// Drop the releases this unit no longer needs — best effort, because the one being replaced may still
    /// be held by the process about to be restarted out of it. A release that will not delete today is
    /// deleted by the next deployment, which is a better answer than failing a run over disk tidiness.
    /// </summary>
    private static void Prune(string path, string keep)
    {
        var releases = Path.Combine(path, LocalPaths.Releases);
        if (!Directory.Exists(releases)) return;

        foreach (var old in Directory.EnumerateDirectories(releases)
                     .Where(d => !string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase)))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (IOException) { /* still in use — next time */ }
            catch (UnauthorizedAccessException) { /* likewise */ }
        }
    }

    /// <summary>
    /// Where this unit's files come from. Both failures are terminal and are raised before anything on disk
    /// is touched — the operator named something that is not there, and no amount of retrying conjures it.
    /// </summary>
    private static string Source(UnitContext context)
    {
        var name = context.Option(LocalOptions.Source)
            ?? throw new LocalConfigurationException(context.Name,
                $"Unit '{context.Name}' is a directory but names no '{LocalOptions.Source}' — " +
                "say which part of the artifact it is made of.");

        // Core's check, not ours: the AWS provider wrote the same one, and an operator should not get a
        // different sentence about the same mistake depending on where they deployed.
        return context.Artifact.RequirePart(name, ArtifactPart.Directory);
    }
}

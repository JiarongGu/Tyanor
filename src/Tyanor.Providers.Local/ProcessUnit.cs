using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Tyanor.Providers.Local;

/// <summary>
/// A unit that IS a long-lived process on this machine — the self-hosted server half of a machine
/// deployment.
///
/// <para><b>This is where the engine's model gets its hardest test.</b> A cloud provider is handed
/// <see cref="UnitPhase.Converging"/> by a control plane that keeps working whether or not anyone is
/// watching. Here there is no control plane: the only durable trace of a running server is its pid, and
/// the only evidence it is READY is that something answers on its port. Both are readable by any process
/// on the machine, which is what lets a second run attach to a server the first run started rather than
/// launching a competing one.</para>
///
/// <para>The process is started detached. It outlives the deploy that created it — a server that died
/// when the tool exited would not be a deployment.</para>
/// </summary>
/// <param name="root">The machine's deployment root.</param>
internal sealed class ProcessUnit(string root) : IUnitDriver
{
    // Long enough that a starting server is not hammered, short enough that a fast one is not made to look
    // slow. Nothing depends on the exact value.
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The full table. <see cref="UnitPhase.Converging"/> is the one that earns its keep: a process that is
    /// alive but not yet answering is work in flight, and re-issuing against it starts a second server on
    /// a port only one of them can have.
    /// </summary>
    public async Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        var record = Records.Read<ProcessRecord>(PidPath(context));
        if (record is null) return UnitPhase.Missing;                  // never started, or cleanly removed

        using var process = Running(record);
        // Recorded but not there: it crashed, or someone killed it. The pid file is stale wreckage rather
        // than a handle on anything, so the unit is Broken and the engine clears it and starts fresh.
        if (process is null) return UnitPhase.Broken;

        if (await AnsweringAsync(context)) return UnitPhase.Ready;

        // Alive but silent. Inside the grace window that is a server still booting; past it, it is a server
        // that is not going to boot, and calling that Converging forever would hang the run instead of
        // telling anyone.
        return DateTimeOffset.UtcNow < record.StartedAt + Grace(context)
            ? UnitPhase.Converging
            : UnitPhase.Broken;
    }

    /// <summary>Start it and return. The engine does the waiting, so attaching uses the identical wait.</summary>
    public Task CreateAsync(UnitContext context)
    {
        var command = Command(context);
        var workingDirectory = WorkingDirectory(context);
        Directory.CreateDirectory(workingDirectory);

        var info = new ProcessStartInfo(command)
        {
            Arguments = context.Option(LocalOptions.Arguments) ?? "",
            WorkingDirectory = workingDirectory,
            // Not redirected on purpose: the server's output goes wherever the caller's does. A library
            // that captures an operator's logs into a pipe nobody reads has decided something that is not
            // its to decide — and an unread pipe eventually blocks the process it was meant to supervise.
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)
            ?? throw LocalDeploymentException.Hard(context.Name, $"'{command}' started no process.");

        Records.Write(PidPath(context), new ProcessRecord(
            process.Id, new DateTimeOffset(process.StartTime), command, Desired(context)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A process takes new configuration by being restarted, so an update is a stop and a start — but only
    /// when something it depends on actually moved. The fingerprint covers the command, its arguments and
    /// the CONTENT it serves, which is how a new build of the watched directory restarts the server and a
    /// re-run of the same build does not.
    /// </summary>
    public async Task<bool> UpdateAsync(UnitContext context)
    {
        var record = Records.Read<ProcessRecord>(PidPath(context));
        if (record is not null && record.Fingerprint == Desired(context)) return false;

        await RemoveAsync(context);
        await CreateAsync(context);
        return true;
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(UnitContext context)
    {
        var path = PidPath(context);
        if (Records.Read<ProcessRecord>(path) is { } record)
        {
            using var process = Running(record);
            if (process is not null)
            {
                // The tree, not the process: a server that forked workers leaves them holding the port,
                // and the next create would fail against something the operator cannot see.
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* exited between the check and the kill — the goal */ }
                await process.WaitForExitAsync(context.Cancellation);
            }
        }
        Records.Delete(path);
    }

    /// <summary>
    /// Wait for the server to answer: it does, or it is gone, or it runs out of grace. The difference
    /// between the last two is the difference between "fix your command" and "try again".
    /// </summary>
    /// <param name="context">The unit, and the progress callback that makes a slow boot look like a slow
    /// boot rather than a hang.</param>
    /// <exception cref="LocalDeploymentException">
    /// HARD when the process is gone — it exited during startup, and starting it again produces the same
    /// crash. TRANSIENT when the grace window expires with it still alive: nothing about the desired state
    /// is wrong, so the run pauses and the operator can resume, at which point the phase read decides
    /// whether it was merely slow or is genuinely broken.
    /// </exception>
    public async Task AwaitSettledAsync(UnitContext context)
    {
        var polls = 0;
        while (true)
        {
            context.ThrowIfCancelled();

            var record = Records.Read<ProcessRecord>(PidPath(context))
                ?? throw LocalDeploymentException.Hard(context.Name,
                    $"{context.Label}: nothing recorded a running process — it was removed while starting.");

            using (var process = Running(record))
            {
                if (process is null)
                    throw LocalDeploymentException.Hard(context.Name,
                        $"{context.Label}: the process exited while starting. Check the command and its output.");
            }

            if (await AnsweringAsync(context))
            {
                context.Progress($"{context.Label}: answering.", status: ProgressStatus.Success);
                return;
            }

            // The window runs from when the PROCESS started, not from when this wait did. A second run
            // attaching to a server that has been failing to boot for five minutes should not be granted a
            // fresh five.
            if (DateTimeOffset.UtcNow >= record.StartedAt + Grace(context))
                throw LocalDeploymentException.Transient(context.Name,
                    $"{context.Label}: still not answering after {Grace(context).TotalSeconds:0} seconds.");

            if (polls++ % 8 == 0)
                context.Progress($"{context.Label}: started — waiting for it to answer…");
            await Task.Delay(Poll, context.Cancellation);
        }
    }

    /// <summary>
    /// One resource, identified by its pid FILE rather than its pid: the identity has to survive a restart,
    /// and a pid does not. The fingerprint is what it was started to run, so a plan reports a pending
    /// restart as a change to one resource rather than as nothing at all.
    /// </summary>
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
    {
        var path = PidPath(context);
        var record = Records.Read<ProcessRecord>(path);
        if (record is null) return Task.FromResult<IReadOnlyList<ResourceState>>([]);

        using var process = Running(record);
        // Recorded but not running. Refresh reports what IS — and a state that still claimed this would be
        // the difference between an operator knowing their server is down and not.
        return Task.FromResult<IReadOnlyList<ResourceState>>(
            process is null ? [] : [new ResourceState(path, "local/process", record.Fingerprint)]);
    }

    /// <summary>
    /// The process behind a record, or null when it is not there any more.
    ///
    /// <para><b>The start-time check is not defensive detail; it is the whole safety of this provider.</b>
    /// Operating systems reuse pids. A tool that kills whatever currently holds a remembered number will,
    /// eventually, kill something that has nothing to do with the deployment — and it will do it on the
    /// machine of whoever left a deployment lying around longest.</para>
    /// </summary>
    private static Process? Running(ProcessRecord record)
    {
        try
        {
            var process = Process.GetProcessById(record.Pid);
            if (process.HasExited) { process.Dispose(); return null; }

            // A second of tolerance: the value survives a JSON round trip, and no pid is reused within a
            // second of another process starting at the same instant.
            if (Math.Abs((new DateTimeOffset(process.StartTime) - record.StartedAt).TotalSeconds) >= 1)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException) { return null; }          // no process with that id
        catch (InvalidOperationException) { return null; }   // it exited underneath us
    }

    /// <summary>
    /// Whether the server is answering. With no port configured this is <c>true</c> the moment the process
    /// is alive — stated plainly rather than dressed up: without something to probe, "the process exists"
    /// is the most Tyanor can honestly claim, and pretending to a health check nobody configured would be
    /// worse than admitting the limit.
    /// </summary>
    private static async Task<bool> AnsweringAsync(UnitContext context)
    {
        if (Port(context) is not { } port) return true;

        try
        {
            using var client = new TcpClient();
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
            probe.CancelAfter(ProbeTimeout);
            await client.ConnectAsync(IPAddress.Loopback, port, probe.Token);
            return true;
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }            // refused: not up yet, which is an answer
    }

    /// <summary>
    /// Resolve everything a create would resolve, and report every failure rather than the first.
    /// </summary>
    /// <remarks>
    /// Each is resolved separately on purpose: an operator with both a missing command and an unparseable
    /// port should be told both, not told one and then the other on the next attempt.
    /// </remarks>
    public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        new UnitProblems()
            .Check(() => Command(context))
            .Check(() => Port(context))
            .Check(() => Grace(context))
            .Found();

    /// <summary>Where the server is answering, when it was given a port to answer on.</summary>
    public Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var record = Records.Read<ProcessRecord>(PidPath(context));

        if (record is not null)
        {
            using var process = Running(record);
            if (process is not null)
            {
                outputs[$"{context.Name}.pid"] = record.Pid.ToString();
                if (Port(context) is { } port) outputs[$"{context.Name}.url"] = $"http://localhost:{port}";
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(outputs);
    }

    private string PidPath(UnitContext context) =>
        Path.Combine(LocalPaths.Bookkeeping(root, context), $"{context.Name}.pid.json");

    private string Desired(UnitContext context)
    {
        // The content the server serves is part of what it IS: a new build in the watched directory is a
        // different service, even though the command line has not changed a character.
        var watched = context.Option(LocalOptions.Watch);
        var content = watched is null
            ? null
            : Records.Read<UnitMarker>(LocalPaths.Marker(root, context.Request, watched))?.Content;

        return Fingerprints.Of(
            Command(context),
            context.Option(LocalOptions.Arguments),
            WorkingDirectory(context),
            content);
    }

    private static string Command(UnitContext context) =>
        context.Option(LocalOptions.Command)
        ?? throw new LocalConfigurationException(context.Name,
            $"Unit '{context.Name}' is a process but names no '{LocalOptions.Command}'.");

    private string WorkingDirectory(UnitContext context)
    {
        if (context.Option(LocalOptions.WorkingDirectory) is { } explicitly) return explicitly;

        // The watched unit's CURRENT RELEASE, not its directory — which is what makes "run the thing I
        // just unpacked" need no configuration, and is also why a new build can be written while this one
        // is still running: the two are never the same folder.
        if (context.Option(LocalOptions.Watch) is { } watched)
            return LocalPaths.CurrentRelease(root, context.Request, watched) ?? LocalPaths.Unit(root, context.Request, watched);

        return Path.Combine(root, context.Request.Prefix);
    }

    private static int? Port(UnitContext context)
    {
        if (context.Option(LocalOptions.HealthPort) is not { } raw) return null;
        return int.TryParse(raw, out var port) && port is > 0 and < 65536
            ? port
            : throw new LocalConfigurationException(context.Name,
                $"'{LocalOptions.HealthPort}' is '{raw}', which is not a port.");
    }

    private static TimeSpan Grace(UnitContext context)
    {
        if (context.Option(LocalOptions.HealthSeconds) is not { } raw)
            return TimeSpan.FromSeconds(LocalOptions.DefaultHealthSeconds);

        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new LocalConfigurationException(context.Name,
                $"'{LocalOptions.HealthSeconds}' is '{raw}', which is not a number of seconds.");
    }
}

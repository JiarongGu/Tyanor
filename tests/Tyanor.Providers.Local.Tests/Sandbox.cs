using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tyanor.Engine;
using Tyanor.Engine.State;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// A throwaway machine: a deployment root, an artifact to deploy from, and a runner wired to both.
///
/// <para>These tests start REAL processes and write REAL files, because that is the only way to test a
/// provider whose whole difficulty is that a machine remembers nothing. A mocked filesystem would agree
/// with whatever the driver believed.</para>
/// </summary>
internal sealed class Sandbox : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "tyanor-tests", Guid.NewGuid().ToString("N")[..12]);

    public Sandbox()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Artifact);
        Target = new LocalTarget(Root);
        State = new FileStateStore(Path.Combine(_base, "state.json"));
        History = new FileRunHistory(Path.Combine(_base, "runs.json"));
        // A real retry policy, only faster. `Attempts: 1` meant NO retry, which quietly contradicted the
        // provider it was testing: a sharing violation is classified Transient precisely so the engine rides
        // it out, and a harness that switches that off turns an ordinary OS hiccup into a failed run. It
        // showed up as this suite failing perhaps one run in three when the machine was busy — a deleting
        // directory still held by a just-exited process — which is the exact case the classification exists
        // for. Four fast attempts is under 200ms in the worst case and is how the engine actually behaves.
        Runner = new ProcedureRunner(Target, History, State,
            new RetryPolicy(Attempts: 4, BaseDelay: TimeSpan.FromMilliseconds(25)));
    }

    /// <summary>Where deployments land.</summary>
    public string Root => Path.Combine(_base, "deploy");

    /// <summary>Stands in for a publish output — the artifact part named "app".</summary>
    public string Artifact => Path.Combine(_base, "artifact");

    public LocalTarget Target { get; }

    public IStateStore State { get; }

    public IRunHistory History { get; }

    public ProcedureRunner Runner { get; }

    /// <summary>Write a file into the artifact, so a test can produce "a new build".</summary>
    public void Publish(string name, string content) => File.WriteAllText(Path.Combine(Artifact, name), content);

    /// <summary>The directory a unit was deployed into — the stable name, not the build.</summary>
    public string Deployed(string prefix, string unit) => Path.Combine(Root, prefix, unit);

    /// <summary>The release currently in service for a directory unit, which is where the files really are.</summary>
    public string Live(string prefix, string unit)
    {
        var marker = Path.Combine(Deployed(prefix, unit), ".tyanor-unit.json");
        var release = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(marker)).GetProperty("Release").GetString();
        return Path.Combine(Deployed(prefix, unit), "releases", release!);
    }

    /// <summary>How many builds are still on disk for a unit.</summary>
    public int Releases(string prefix, string unit)
    {
        var releases = Path.Combine(Deployed(prefix, unit), "releases");
        return Directory.Exists(releases) ? Directory.GetDirectories(releases).Length : 0;
    }

    /// <summary>The pid recorded for a process unit, or null when nothing is recorded.</summary>
    public int? Pid(string prefix, string unit)
    {
        var path = Path.Combine(Root, prefix, ".tyanor", $"{unit}.pid.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)).GetProperty("Pid").GetInt32();
    }

    /// <summary>A command that stays alive until it is killed — the test's stand-in for a server.</summary>
    public static (string Command, string Arguments) Sleeper => OperatingSystem.IsWindows()
        ? ("powershell.exe", "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"")
        : ("/bin/sh", "-c \"sleep 120\"");

    /// <summary>
    /// A port that is guaranteed to REFUSE connections for as long as the holder is alive.
    /// </summary>
    /// <remarks>
    /// <para>Bound but never listening. A socket in that state answers a connection attempt with a reset, so
    /// a health check against it fails exactly as it would against a server that is not up — while the bind
    /// stops any other process taking the port in the meantime.</para>
    /// <para>The previous version bound port 0, read the number, released it, and hoped. That held right up
    /// until the three test assemblies started running in parallel under one <c>dotnet test</c>, at which
    /// point something else occasionally grabbed the released port and a test expecting
    /// <see cref="UnitPhase.Converging"/> got <see cref="UnitPhase.Ready"/>. A suite that fails once in
    /// twenty runs is a suite people stop believing, so this reserves rather than hopes.</para>
    /// </remarks>
    public static PortReservation ReservePort() => new();

    /// <summary>A reserved port that refuses connections until disposed.</summary>
    public sealed class PortReservation : IDisposable
    {
        private readonly Socket _reservation = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        internal PortReservation() => _reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        /// <summary>The reserved port.</summary>
        public int Port => ((IPEndPoint)_reservation.LocalEndPoint!).Port;

        /// <summary>Release it — so a test can then open a real listener on the same port.</summary>
        public void Dispose() => _reservation.Dispose();
    }

    /// <summary>Open <paramref name="port"/> — the test playing the part of the server's own socket.</summary>
    public static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    public void Dispose()
    {
        // Kill first: a live process holds its working directory open, and it is inside the tree below.
        foreach (var pidFile in SafeEnumerate(_base, "*.pid.json"))
        {
            try
            {
                var pid = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(pidFile)).GetProperty("Pid").GetInt32();
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch { /* already gone, or never ours — either way there is nothing to clean up */ }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_base, recursive: true); return; }
            catch (IOException) { Thread.Sleep(100); }        // a handle not yet released
            catch (UnauthorizedAccessException) { return; }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).ToList(); }
        catch (DirectoryNotFoundException) { return []; }
    }
}

using System.ComponentModel;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// The local provider held to the contracts a provider written anywhere else will be held to.
///
/// <para>These run against real files and real processes, which is the point: the contract asks whether the
/// driver behaves the way the engine assumes, and a stub answers by agreeing with whatever the driver
/// believes.</para>
/// </summary>
public class LocalDriverContractTests : IDisposable
{
    private readonly Sandbox _box = new();

    public static TheoryData<string> Checks() => Suites.Names(new UnitDriverContract(null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task A_directory_unit_satisfies(string check)
    {
        _box.Publish("app.dll", "v1");
        return new UnitDriverContract(new LocalFixture(_box, Kind.Directory)).AssertAsync(check);
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public Task A_process_unit_satisfies(string check) =>
        // The harder shape, and the one worth checking: a live process, a pid file, and a phase that has to
        // be inferred rather than asked for.
        new UnitDriverContract(new LocalFixture(_box, Kind.Process)).AssertAsync(check);

    public void Dispose() => _box.Dispose();

    private enum Kind { Directory, Process }

    private sealed class LocalFixture : IUnitDriverFixture
    {
        private readonly Sandbox _box;

        public LocalFixture(Sandbox box, Kind kind)
        {
            _box = box;
            Driver = box.Target.Driver;
            Unit = new ProcedureUnit(kind == Kind.Directory ? "runtime" : "service", "Contract subject");

            var (command, arguments) = Sandbox.Sleeper;
            Request = new DeploymentRequest("contract",
                new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
                kind == Kind.Directory
                    ? new Dictionary<string, string>
                    {
                        ["runtime.kind"] = LocalOptions.DirectoryKind,
                        ["runtime.source"] = "app",
                    }
                    : new Dictionary<string, string>
                    {
                        ["service.kind"] = LocalOptions.ProcessKind,
                        ["service.command"] = command,
                        ["service.args"] = arguments,
                        ["service.workDir"] = box.Root,
                    });
        }

        public IUnitDriver Driver { get; }

        public ProcedureUnit Unit { get; }

        public DeploymentRequest Request { get; }

        // The driver's own remove IS the reset — which is worth noticing, because a provider whose remove
        // does not fully return to nothing fails half this suite, and that is exactly the defect it should.
        public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
    }
}

/// <summary>The local classifier, held to the contract.</summary>
public class LocalClassifierContractTests
{
    private static readonly FailureClassifierContract Suite = new(new LocalFixture());

    public static TheoryData<string> Checks() => Suites.Names(Suite);

    [Theory]
    [MemberData(nameof(Checks))]
    public Task It_satisfies(string check) => Suite.AssertAsync(check);

    private sealed class LocalFixture : IFailureClassifierFixture
    {
        public IFailureClassifier Classifier { get; } = new LocalTarget("unused").Classifier;

        // Real errors this provider genuinely meets, not shapes invented to pass.
        public IReadOnlyList<Exception> CredentialErrors { get; } =
        [
            new UnauthorizedAccessException("Access to the path is denied."),
            new Win32Exception(5),                                  // ERROR_ACCESS_DENIED
            new Win32Exception(13),                                 // EACCES
        ];

        public IReadOnlyList<Exception> TransientErrors { get; } =
        [
            new IOException("in use", unchecked((int)0x80070020)),  // ERROR_SHARING_VIOLATION
            new TimeoutException("the health check timed out"),
            LocalDeploymentException.Transient("service", "still not answering"),
        ];

        public IReadOnlyList<Exception> HardErrors { get; } =
        [
            new Win32Exception(2),                                  // the command does not exist
            new FileNotFoundException(),
            new LocalConfigurationException("service", "names no command"),
            LocalDeploymentException.Hard("service", "the process exited while starting"),
        ];
    }
}

/// <summary>Turning a suite's check names into xUnit cases, so each one reports under its own name.</summary>
internal static class Suites
{
    public static TheoryData<string> Names(ContractSuite suite)
    {
        var data = new TheoryData<string>();
        foreach (var check in suite.Checks) data.Add(check);
        return data;
    }
}

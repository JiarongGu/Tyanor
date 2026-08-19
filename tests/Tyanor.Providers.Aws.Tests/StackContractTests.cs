using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The STACK driver held to the contract every driver is held to — offline.
///
/// <para><b>This was the largest untested surface left in the repository, and it was invisible.</b>
/// <c>UnitDriverContract</c> was run against the stack driver in exactly one place: inside
/// <c>AwsLiveDeploymentTests</c>, behind <c>TYANOR_LIVE_AWS</c>, which returns before doing anything when the
/// variable is unset. Nothing has ever reached AWS from this repository, so the suite had run against this
/// driver zero times — while <c>doctor</c> reported "4 unit kinds, each held to 2 contract suites", because
/// its check could not tell RUNNING a suite from naming one in a file that returns early.</para>
///
/// <para><b>Why this can now run without a cloud, when the reason it could not was real.</b> The stated
/// argument was that a CloudFormation fake would have to encode CloudFormation's semantics — a create
/// settling into <c>CREATE_COMPLETE</c>, a rollback settling somewhere specific — and a suite asserting that
/// is this repository agreeing with itself. That argument is correct about the interesting statuses and
/// wrong about the contract, and the two had been collapsed together.</para>
///
/// <para>Nothing this suite checks is a question about CloudFormation. That a phase read changes nothing,
/// that removing what is already gone does not throw, that an update over an unchanged deployment reports no
/// change, that a resource keeps its id across a refresh, that outputs stop answering once the unit is gone —
/// every one of those is a fact about <b>our driver</b>, and each fails quietly and expensively: as a
/// duplicate deployment, a teardown that will not re-run, a resume that redoes finished work, or a UI still
/// showing an address that stopped resolving.</para>
///
/// <para><b>What is still last-mile, and is untouched by this.</b> Whether AWS accepts the request we build.
/// Whether a real create settles into the status the phase table believes. Whether
/// <c>UPDATE_ROLLBACK_FAILED</c> behaves as D14 assumes. <see cref="StatefulCloudFormation"/> models none of
/// that on purpose — it knows only that a created stack exists and a deleted one does not — so this suite
/// cannot accidentally start certifying AWS's behaviour. That stays where D23 put it.</para>
/// </summary>
public class StackDriverContractTests
{
    /// <summary>Short, because nothing here waits on anything real.</summary>
    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    public static TheoryData<string> Checks() => Suites.Names(new UnitDriverContract(null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public async Task A_stack_unit_satisfies(string check)
    {
        using var fixture = new StackFixture();
        await new UnitDriverContract(fixture).AssertAsync(check);
    }

    /// <summary>
    /// A stack unit with a template on disk — the driver resolves a real artifact part before it calls AWS,
    /// so the part has to be real even when the cloud is not.
    /// </summary>
    private sealed class StackFixture : IUnitDriverFixture, IDisposable
    {
        private readonly StatefulCloudFormation _cfn = new();
        private readonly string _dir;

        public StackFixture()
        {
            var s3 = new FakeS3();
            _cfn.Outputs["markername"] = "tyanor-contract-marker";

            Driver = new StackUnit(_cfn, s3, new AwsAccount(new FakeSts()), "ap-southeast-2", NoWait);

            _dir = Path.Combine(Path.GetTempPath(), "tyanor-sc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
            var template = Path.Combine(_dir, "marker.template.json");
            File.WriteAllText(template, "{}");

            Request = new DeploymentRequest("contract",
                new DeploymentArtifact(new Dictionary<string, string> { ["template"] = template }),
                new Dictionary<string, string>
                {
                    ["marker.kind"] = AwsOptions.StackKind,
                    ["marker.template"] = "template",
                });
        }

        public IUnitDriver Driver { get; }

        public ProcedureUnit Unit { get; } = new("marker", "Marker");

        public DeploymentRequest Request { get; }

        /// <summary>
        /// The stack exposes one output, so the suite can check it appears once deployed and is gone after a
        /// remove — the direction that fails quietly, when outputs are read from a stored copy rather than
        /// from the target.
        /// </summary>
        public IReadOnlyCollection<string> ExpectedOutputs { get; } = ["markername"];

        public Task ResetAsync(CancellationToken ct) =>
            Driver.RemoveAsync(new UnitContext(Unit, Request, _ => { }, ct));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
        }
    }
}

using Tyanor.Engine;
using Tyanor.Engine.State;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// An application's own step sitting in the same procedure as CloudFormation stacks.
///
/// <para><b>The capability was claimed for every provider and proven on one.</b> D19 and the README both
/// say a consumer registers their own unit kind inside a shipped provider; `AwsTarget` takes a
/// <see cref="CustomUnits"/> and passes it on, and nothing anywhere verified that it arrives — the local
/// provider had these tests and AWS did not. "It compiles" is the weaker claim, and it is the one that was
/// being made.</para>
///
/// <para>This matters more here than on the local provider, because AWS is where the interesting mixture
/// lives: a migration check that only means something to one application, declared between two stacks that
/// mean nothing to it.</para>
/// </summary>
public class CustomUnitTests
{
    private static readonly ProcedureUnit Api = new("api", "API");
    private static readonly ProcedureUnit Migration = new("migration", "Database changes");
    private static readonly Procedure Site = new("site", [Api, Migration]);

    /// <summary>A target over the fakes, so nothing here needs an account.</summary>
    private static (AwsUnitDriver Driver, IFailureClassifier Classifier, FakeCloudFormation Cfn) Build(
        CustomUnits custom)
    {
        var cfn = new FakeCloudFormation();
        var driver = new AwsUnitDriver(
            cfn, new FakeS3(), new FakeCloudFront(), new AwsAccount(new FakeSts()), "ap-southeast-2", custom);

        return (driver, FailureClassifiers.Chain(new AwsFailureClassifier(), custom.Classifier), cfn);
    }

    private static (DeploymentRequest Request, string Dir) Request()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tyanor-custom-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "api.template.json"), "{}");

        return (new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string> { ["template"] = Path.Combine(dir, "api.template.json") }),
            new Dictionary<string, string>
            {
                ["api.kind"] = AwsOptions.StackKind,
                ["api.template"] = "template",
                ["migration.kind"] = "migration",          // the application's own kind
            }), dir);
    }

    [Fact]
    public async Task A_custom_kind_is_DISPATCHED_to_the_application_unit_and_not_to_CloudFormation()
    {
        var migration = new RecordingUnit();
        var (driver, _, cfn) = Build(new CustomUnits { ["migration"] = migration });
        var (request, dir) = Request();

        try
        {
            await driver.PhaseAsync(new UnitContext(Migration, request));
            await driver.CreateAsync(new UnitContext(Migration, request));

            Assert.Equal(["phase", "create"], migration.Calls);
            Assert.Empty(cfn.Requests);                 // CloudFormation was never asked about it
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_STACK_still_goes_to_CloudFormation_with_a_custom_kind_registered()
    {
        // Registering one's own kind must not disturb the provider's own dispatch.
        var (driver, _, cfn) = Build(new CustomUnits { ["migration"] = new RecordingUnit() });
        var (request, dir) = Request();

        try
        {
            Assert.Equal(UnitPhase.Missing, await driver.PhaseAsync(new UnitContext(Api, request)));
            Assert.NotEmpty(cfn.Requests);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_custom_kind_may_NOT_take_the_name_of_one_the_provider_already_has()
    {
        // Built-in kinds are registered first, so a collision is refused rather than silently changing what
        // every existing procedure means.
        var custom = new CustomUnits { [AwsOptions.StackKind] = new RecordingUnit() };

        Assert.Throws<ArgumentException>(() => new AwsUnitDriver(
            new FakeCloudFormation(), new FakeS3(), new FakeCloudFront(),
            new AwsAccount(new FakeSts()), "ap-southeast-2", custom));
    }

    [Fact]
    public async Task A_unit_declaring_an_UNREGISTERED_kind_still_names_the_ones_that_exist()
    {
        var (driver, _, _) = Build(new CustomUnits { ["migration"] = new RecordingUnit() });
        var (request, dir) = Request();

        try
        {
            var thrown = await Assert.ThrowsAsync<UnitKindException>(() => driver.PhaseAsync(
                new UnitContext(new ProcedureUnit("other", "Other"), request with
                {
                    Options = new Dictionary<string, string> { ["other.kind"] = "nope" },
                })));

            Assert.Contains("migration", thrown.Message);           // …including the application's own
            Assert.Contains(AwsOptions.StackKind, thrown.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_applications_classifier_is_chained_AFTER_the_providers_own()
    {
        // Without this a custom unit could never PAUSE: its errors mean nothing to the AWS classifier, which
        // correctly returns null, and the engine's default for null is Hard. The provider goes first because
        // it knows its own SDK; "not mine" is a real answer, which is what makes chaining work at all.
        var custom = new CustomUnits { Classifier = new MigrationClassifier(), ["migration"] = new RecordingUnit() };
        var (_, classifier, _) = Build(custom);

        Assert.Equal(FailureClass.Transient, classifier.Classify(new MigrationNotReady()));
        Assert.Equal(FailureClass.Credentials,
            classifier.Classify(new Amazon.Runtime.AmazonServiceException("no") { ErrorCode = "ExpiredToken" }));
        Assert.Null(classifier.Classify(new InvalidOperationException("nobody's")));
    }

    [Fact]
    public async Task A_custom_units_transient_failure_PAUSES_the_run_rather_than_ending_it()
    {
        // The whole point of the chain, end to end through the engine.
        var custom = new CustomUnits
        {
            Classifier = new MigrationClassifier(),
            ["migration"] = new RecordingUnit { Throws = new MigrationNotReady() },
        };
        var (driver, classifier, _) = Build(custom);
        var (request, dir) = Request();

        try
        {
            var runner = new ProcedureRunner(new Target(driver, classifier), new InMemoryRunHistory(), null,
                new RetryPolicy(Attempts: 1));

            var outcome = await runner.ApplyAsync(new Procedure("site", [Migration]), request);

            Assert.True(outcome.Resumable);
            Assert.Equal("transient", outcome.Reason?.Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The application's step. Records what the engine asked of it.</summary>
    private sealed class RecordingUnit : IUnitDriver
    {
        public List<string> Calls { get; } = [];

        public Exception? Throws { get; init; }

        public Task<UnitPhase> PhaseAsync(UnitContext c)
        {
            Calls.Add("phase");
            return Throws is null ? Task.FromResult(UnitPhase.Missing) : throw Throws;
        }

        public Task CreateAsync(UnitContext c) { Calls.Add("create"); return Task.CompletedTask; }
        public Task<bool> UpdateAsync(UnitContext c) { Calls.Add("update"); return Task.FromResult(false); }
        public Task RemoveAsync(UnitContext c) { Calls.Add("remove"); return Task.CompletedTask; }
        public Task AwaitSettledAsync(UnitContext c) => Task.CompletedTask;
        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
            Task.FromResult<IReadOnlyList<ResourceState>>([]);
    }

    /// <summary>"The endpoint is not warm yet" — the application's own transient.</summary>
    private sealed class MigrationNotReady : Exception;

    private sealed class MigrationClassifier : IFailureClassifier
    {
        public FailureClass? Classify(Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
                if (e is MigrationNotReady) return FailureClass.Transient;

            return null;    // not mine — the next classifier gets its turn
        }
    }

    /// <summary>A target over the driver and chained classifier, so the ENGINE runs the whole thing.</summary>
    private sealed class Target(IUnitDriver driver, IFailureClassifier classifier) : IDeploymentTarget
    {
        public string Id => "aws";
        public IUnitDriver Driver => driver;
        public IFailureClassifier Classifier => classifier;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) =>
            Task.FromResult(new TargetIdentity(true));
    }
}

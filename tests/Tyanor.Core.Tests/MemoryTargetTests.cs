using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The shipped test target, held to the contracts a provider written anywhere else is held to.
///
/// <para>This is the point of it. A test target that did not satisfy <see cref="UnitDriverContract"/> would
/// teach every consumer using it a wrong belief about how a provider behaves — their procedure would pass
/// against it and fail against AWS, which is worse than having no test target at all.</para>
/// </summary>
public class MemoryTargetContractTests
{
    public static TheoryData<string> DriverChecks() => Suites.Names(new UnitDriverContract(null!));

    public static TheoryData<string> ClassifierChecks() => Suites.Names(new FailureClassifierContract(null!));

    [Theory]
    [MemberData(nameof(DriverChecks))]
    public Task It_satisfies_the_driver_contract(string check) =>
        new UnitDriverContract(new MemoryFixture()).AssertAsync(check);

    [Theory]
    [MemberData(nameof(ClassifierChecks))]
    public Task Its_classifier_satisfies_the_contract(string check) =>
        new FailureClassifierContract(new ClassifierFixture()).AssertAsync(check);

    private sealed class MemoryFixture : IUnitDriverFixture
    {
        private readonly MemoryTarget _target = new();

        public MemoryFixture() =>
            // Scripted so the outputs checks have something to catch. A fixture that produces nothing makes
            // them vacuous — they would be verifying that emptiness is empty.
            _target.Outputs["web"] = new Dictionary<string, string> { ["web.url"] = "memory://web" };

        public IUnitDriver Driver => _target;

        public ProcedureUnit Unit { get; } = new("web", "Website");

        public DeploymentRequest Request { get; } =
            new("contract", new DeploymentArtifact(new Dictionary<string, string>()));

        public IReadOnlyCollection<string> ExpectedOutputs { get; } = ["web.url"];

        public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
    }

    private sealed class ClassifierFixture : IFailureClassifierFixture
    {
        public IFailureClassifier Classifier { get; } = new MemoryTarget();

        public IReadOnlyList<Exception> CredentialErrors { get; } =
            [new MemoryFaultException(FailureClass.Credentials, "the token expired")];

        public IReadOnlyList<Exception> TransientErrors { get; } =
            [new MemoryFaultException(FailureClass.Transient, "throttled")];

        public IReadOnlyList<Exception> HardErrors { get; } =
            [new MemoryFaultException(FailureClass.Hard, "the template is malformed")];
    }
}

/// <summary>
/// What a consumer's own test would actually do with it — reaching the states that are expensive to reach
/// against a real target, which is the whole reason this ships.
/// </summary>
public class MemoryTargetTests
{
    private static readonly Procedure Site = new("site",
        [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API")]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    private static ProcedureRunner Runner(MemoryTarget target, IStateStore? state = null) =>
        new(target, new InMemoryRunHistory(), state, new RetryPolicy(Attempts: 1));

    [Fact]
    public async Task Out_of_the_box_it_just_deploys()
    {
        var target = new MemoryTarget();

        Assert.True((await Runner(target).ApplyAsync(Site, Request())).Ok);

        Assert.Equal(["db", "api"], target.Deployed);
        Assert.Equal(["db:create", "api:create"], target.Calls);
    }

    [Fact]
    public async Task A_second_apply_changes_nothing_because_nothing_changed()
    {
        // The property a resume rests on, and the reason Revision exists rather than update always
        // reporting a change.
        var target = new MemoryTarget();
        var runner = Runner(target);
        await runner.ApplyAsync(Site, Request());
        target.Calls.Clear();

        Assert.True((await runner.ApplyAsync(Site, Request())).Ok);

        Assert.Equal(["db:update", "api:update"], target.Calls);   // …and both reported no change
    }

    [Fact]
    public async Task A_NEW_build_is_re_deployed()
    {
        var target = new MemoryTarget();
        var runner = Runner(target);
        await runner.ApplyAsync(Site, Request());

        target.Revision++;                                          // "a new build"
        var plan = await runner.PlanAsync(Site, Request(), RunKind.Apply);

        Assert.True((await runner.ApplyAsync(Site, Request())).Ok);
        Assert.Equal(2, plan.Changes.Count);
    }

    [Fact]
    public async Task A_credential_failure_PAUSES_the_run_so_an_application_can_offer_a_resume()
    {
        // The case this target exists for: reaching it against AWS means deliberately breaking credentials.
        var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");

        var outcome = await Runner(target).ApplyAsync(Site, Request());

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal("credentials", outcome.Reason?.Value);
        Assert.Equal(["db"], target.Deployed);                      // …and what got done stayed done
    }

    [Fact]
    public async Task A_hard_failure_is_terminal()
    {
        var target = new MemoryTarget().Fails("api", FailureClass.Hard, "the template is malformed");

        Assert.False((await Runner(target).ApplyAsync(Site, Request())).Resumable);
    }

    [Fact]
    public async Task A_transient_blip_is_ridden_out_by_the_retry()
    {
        var target = new MemoryTarget().FailsOnce("api");
        var runner = new ProcedureRunner(target, new InMemoryRunHistory(), null,
            new RetryPolicy(Attempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.True((await runner.ApplyAsync(Site, Request())).Ok);
        Assert.Equal(2, target.Attempts["api"]);
    }

    [Fact]
    public async Task Work_already_in_flight_is_ATTACHED_to_rather_than_re_issued()
    {
        // Converging is a state a real target reaches by timing, which a test cannot arrange — so this is
        // the one place the target reports something other than the truth, on purpose.
        var target = new MemoryTarget().Reports("api", UnitPhase.Converging);

        await Runner(target).ApplyAsync(Site, Request());

        Assert.Contains("api:await", target.Calls);
        Assert.DoesNotContain("api:create", target.Calls);
        Assert.DoesNotContain("api:update", target.Calls);
    }

    [Fact]
    public async Task A_broken_unit_is_replaced_and_the_plan_says_so_first()
    {
        var target = new MemoryTarget().Reports("db", UnitPhase.Broken);

        var plan = await Runner(target).PlanAsync(Site, Request());

        Assert.True(plan.IsDestructive);
        Assert.Equal("db", Assert.Single(plan.Replacements).Unit.Name);
    }

    [Fact]
    public async Task A_deployment_that_was_already_there_is_adopted_rather_than_recreated()
    {
        var target = new MemoryTarget().AlreadyDeployed("db", "api");

        var plan = await Runner(target).PlanAsync(Site, Request());

        Assert.All(plan.Steps, s => Assert.Equal(ReconcileAction.Update, s.Action));
    }

    [Fact]
    public async Task Validation_problems_can_be_scripted_so_a_validation_screen_can_be_tested()
    {
        var target = new MemoryTarget();
        target.Problems["api"] = ["names no template", "names no source"];

        var validation = await Runner(target).ValidateAsync(Site, Request());

        Assert.False(validation.Ok);
        Assert.Equal(2, validation.Problems.Count);
        Assert.All(validation.Problems, p => Assert.Equal("api", p.Unit));
    }

    [Fact]
    public async Task Outputs_appear_only_once_the_unit_is_deployed()
    {
        // So a "your site is at …" view can be tested both before and after, which is when it is wrong.
        var target = new MemoryTarget();
        target.Outputs["api"] = new Dictionary<string, string> { ["api.url"] = "https://example.test" };
        var runner = Runner(target);

        Assert.Empty(await runner.OutputsAsync(Site, Request()));

        await runner.ApplyAsync(Site, Request());

        Assert.Equal("https://example.test", (await runner.OutputsAsync(Site, Request()))["api.url"]);
    }

    [Fact]
    public async Task Progress_is_reported_through_the_unit_and_rescaled_into_the_run()
    {
        var target = new MemoryTarget();
        target.Progress["db"] = 50;
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(Site, Request(), seen.Add);

        // Halfway through the first of two equal units is a quarter of the run.
        Assert.Equal(25, Assert.Single(seen, r => r.Message.EndsWith("working…", StringComparison.Ordinal)).Percent);
    }

    [Fact]
    public async Task A_teardown_removes_in_reverse_and_leaves_nothing()
    {
        var target = new MemoryTarget();
        var runner = Runner(target, new InMemoryStateStore());
        await runner.ApplyAsync(Site, Request());
        target.Calls.Clear();

        var plan = await runner.PlanAsync(Site, Request(), RunKind.Destroy);
        Assert.Equal(2, plan.ToDestroy);
        Assert.True(plan.IsDestructive);

        Assert.True((await runner.DestroyAsync(Site, Request())).Ok);

        Assert.Equal(["api:remove", "db:remove"], target.Calls);
        Assert.Empty(target.Deployed);
    }

    [Fact]
    public async Task Refused_credentials_can_be_scripted_so_that_screen_can_be_tested_too()
    {
        var target = new MemoryTarget { Identity = new TargetIdentity(false, Error: "these keys are not valid") };

        var identity = await target.ValidateAsync(null, CancellationToken.None);

        Assert.False(identity.Ok);
        Assert.Equal("these keys are not valid", identity.Error);
    }

    [Fact]
    public async Task Drift_can_be_produced_so_a_drift_view_can_be_tested_and_repaired()
    {
        // Someone changed the deployment outside Tyanor. Distinct from bumping Revision, which is a new
        // build WAITING to go out rather than the deployment moving underneath you — the first test written
        // here confused the two, which is why the two now have separate names.
        var target = new MemoryTarget();
        var runner = Runner(target, new InMemoryStateStore());
        await runner.ApplyAsync(Site, Request());

        target.Drifted("db", "api");

        var drifted = await runner.PlanAsync(Site, Request());
        Assert.True(drifted.HasDrift);
        Assert.Equal("0 to add, 2 to change, 0 to destroy", drifted.Summary);

        // …and applying repairs it, which is the other half an application needs to show.
        Assert.True((await runner.ApplyAsync(Site, Request())).Ok);
        Assert.False((await runner.PlanAsync(Site, Request())).HasDrift);
    }

    [Fact]
    public async Task A_new_BUILD_is_not_reported_as_drift()
    {
        // The mirror image, and the reason the two are separate: a plan compares state to reality, and a
        // build that has not been deployed yet has changed neither.
        var target = new MemoryTarget();
        var runner = Runner(target, new InMemoryStateStore());
        await runner.ApplyAsync(Site, Request());

        target.Revision++;

        Assert.False((await runner.PlanAsync(Site, Request())).HasDrift);
    }

    [Fact]
    public void Its_id_can_be_changed_so_multi_target_wiring_can_be_tested()
    {
        var targets = new DeploymentTargets(
            new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        Assert.Equal(["aws", "local"], targets.Ids);
    }
}

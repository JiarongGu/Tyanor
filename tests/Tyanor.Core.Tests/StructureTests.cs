using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The seams a provider written OUTSIDE this repository builds against.
///
/// <para>These matter more than their size suggests. Everything here is public surface a third party
/// depends on, so a mistake is expensive twice over — once when it misbehaves, and again when it cannot be
/// fixed without breaking someone.</para>
/// </summary>
public class DeploymentTargetsTests
{
    [Fact]
    public void Targets_are_selected_by_id()
    {
        var targets = new DeploymentTargets(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        Assert.Equal("aws", targets.Get("aws").Id);
        Assert.Equal("local", targets.Get("local").Id);
        Assert.Equal(["aws", "local"], targets.Ids);
    }

    [Fact]
    public void An_id_is_matched_however_it_is_cased()
        // A provider id is typed by a person into a config file, and rejecting "AWS" would be a support
        // question rather than a safety property.
        => Assert.NotNull(new DeploymentTargets(new MemoryTarget { Id = "aws" }).TryGet("AWS"));

    [Fact]
    public void Asking_for_a_target_that_is_not_registered_names_the_ones_that_are()
    {
        var targets = new DeploymentTargets(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        var error = Assert.Throws<ArgumentException>(() => targets.Get("kubernetes"));

        Assert.Contains("aws", error.Message);
        Assert.Contains("local", error.Message);
    }

    [Fact]
    public void Two_targets_claiming_one_id_is_refused_rather_than_resolved_by_order()
        // Last-one-wins here is a wrong deployment produced by a wiring detail, and it is undiscoverable:
        // the plan would be computed against the wrong target too, so it would agree.
        => Assert.Throws<ArgumentException>(() => new DeploymentTargets(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "aws" }));

    [Fact]
    public void A_target_with_no_id_is_refused_because_nothing_could_ask_for_it()
        => Assert.Throws<ArgumentException>(() => new DeploymentTargets(new MemoryTarget { Id = "  " }));

    [Fact]
    public void The_single_target_is_the_ordinary_case()
        => Assert.Equal("local", new DeploymentTargets(new MemoryTarget { Id = "local" }).Single().Id);

    [Fact]
    public void Asking_for_THE_target_when_there_are_several_throws_rather_than_picking()
    {
        // The bug this whole type exists for. Registering a second provider must not silently change which
        // one a runner deploys to.
        var targets = new DeploymentTargets(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        var error = Assert.Throws<InvalidOperationException>(() => targets.Single());

        Assert.Contains("aws", error.Message);
        Assert.Contains("local", error.Message);
    }

    [Fact]
    public void Asking_for_THE_target_when_there_are_none_says_so()
        => Assert.Throws<InvalidOperationException>(() => new DeploymentTargets().Single());
}

/// <summary>The kind dispatch, which both shipped providers use and a third would have rewritten.</summary>
public class UnitKindDriverTests
{
    private sealed class Stub(UnitPhase phase) : IUnitDriver
    {
        public Task<UnitPhase> PhaseAsync(UnitContext c) => Task.FromResult(phase);
        public Task CreateAsync(UnitContext c) => Task.CompletedTask;
        public Task<bool> UpdateAsync(UnitContext c) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext c) => Task.CompletedTask;
        public Task AwaitSettledAsync(UnitContext c) => Task.CompletedTask;
        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c)
            => Task.FromResult<IReadOnlyList<ResourceState>>([]);
    }

    private sealed class TwoKinds : UnitKindDriver
    {
        public TwoKinds()
        {
            Register("directory", new Stub(UnitPhase.Ready));
            Register("process", new Stub(UnitPhase.Converging));
        }
    }

    private static readonly ProcedureUnit Unit = new("web", "Website");

    private static DeploymentRequest Request(params (string Key, string Value)[] options) =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()),
            options.ToDictionary(o => o.Key, o => o.Value));

    [Fact]
    public async Task Each_unit_goes_to_the_kind_it_declared()
    {
        var driver = new TwoKinds();

        Assert.Equal(UnitPhase.Ready, await driver.PhaseAsync(new UnitContext(Unit, Request(("web.kind", "directory")))));
        Assert.Equal(UnitPhase.Converging, await driver.PhaseAsync(new UnitContext(Unit, Request(("web.kind", "process")))));
    }

    [Fact]
    public async Task An_unscoped_kind_covers_every_unit_that_does_not_disagree()
        => Assert.Equal(UnitPhase.Ready, await new TwoKinds().PhaseAsync(new UnitContext(Unit, Request(("kind", "directory")))));

    [Fact]
    public async Task A_unit_that_declares_no_kind_is_refused_and_told_what_the_choices_are()
    {
        // No default, not even with one kind registered: guessing deploys something the operator never
        // described, and the moment a second kind is added every unit relying on the default changes
        // meaning without changing text.
        var error = await Assert.ThrowsAsync<UnitKindException>(
            () => new TwoKinds().PhaseAsync(new UnitContext(Unit, Request())));

        Assert.Contains("directory", error.Message);
        Assert.Contains("process", error.Message);
    }

    [Fact]
    public async Task A_kind_the_provider_does_not_have_is_refused()
    {
        var error = await Assert.ThrowsAsync<UnitKindException>(
            () => new TwoKinds().PhaseAsync(new UnitContext(Unit, Request(("web.kind", "lambda")))));

        Assert.Contains("lambda", error.Message);
    }

    [Fact]
    public async Task A_kind_is_matched_however_it_is_cased()
        => Assert.Equal(UnitPhase.Ready,
            await new TwoKinds().PhaseAsync(new UnitContext(Unit, Request(("web.kind", "Directory")))));

    [Fact]
    public void Registering_one_kind_twice_is_a_mistake_worth_refusing()
    {
        // Silently replacing the first would make a provider's behaviour depend on constructor line order.
        var driver = new Duplicating();

        Assert.Throws<ArgumentException>(driver.RegisterTwice);
    }

    [Fact]
    public void A_kind_error_is_a_DEFINITION_error()
        // So a consumer can show "you configured this wrongly" without matching on message text.
        => Assert.IsAssignableFrom<DefinitionException>(new UnitKindException("x"));

    private sealed class Duplicating : UnitKindDriver
    {
        public void RegisterTwice()
        {
            Register("directory", new Stub(UnitPhase.Ready));
            Register("directory", new Stub(UnitPhase.Missing));
        }
    }
}

/// <summary>Resolving artifact parts — shared so every provider refuses a missing build the same way.</summary>
public class ArtifactPartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyanor-part-" + Guid.NewGuid().ToString("N")[..8]);

    public ArtifactPartTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "template.json"), "{}");
    }

    private DeploymentArtifact Artifact() => new(new Dictionary<string, string>
    {
        ["bundle"] = _dir,
        ["template"] = Path.Combine(_dir, "template.json"),
        ["never-built"] = Path.Combine(_dir, "does-not-exist"),
    });

    [Fact]
    public void A_part_that_is_there_comes_back()
    {
        Assert.Equal(_dir, Artifact().RequirePart("bundle", ArtifactPart.Directory));
        Assert.EndsWith("template.json", Artifact().RequirePart("template", ArtifactPart.File));
    }

    [Fact]
    public void A_part_the_artifact_does_not_carry_names_the_ones_it_does()
    {
        var error = Assert.Throws<ArtifactException>(() => Artifact().RequirePart("nope"));

        Assert.Contains("bundle", error.Message);
        Assert.Contains("template", error.Message);
    }

    [Fact]
    public void A_part_pointing_at_nothing_says_build_first()
    {
        // The common one: the procedure is right and the build has not run.
        var error = Assert.Throws<ArtifactException>(() => Artifact().RequirePart("never-built"));

        Assert.Contains("Build first", error.Message);
    }

    [Fact]
    public void A_part_of_the_wrong_shape_is_refused()
    {
        // A template that is a directory, or a bundle that is a file, fails later and far more confusingly.
        Assert.Throws<ArtifactException>(() => Artifact().RequirePart("bundle", ArtifactPart.File));
        Assert.Throws<ArtifactException>(() => Artifact().RequirePart("template", ArtifactPart.Directory));
    }

    [Fact]
    public void An_artifact_error_is_a_DEFINITION_error()
        => Assert.IsAssignableFrom<DefinitionException>(new ArtifactException("x"));

    [Fact]
    public void An_empty_artifact_says_it_carries_nothing_rather_than_nothing_at_all()
    {
        var error = Assert.Throws<ArtifactException>(
            () => new DeploymentArtifact(new Dictionary<string, string>()).RequirePart("web"));

        Assert.Contains("nothing", error.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }
}

using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// A fixture that LOOKS like it overrides a default member and does not.
///
/// <para><b>Found from outside, because it cannot be found from in here.</b> Every fixture in this
/// repository implements <see cref="IUnitDriverFixture"/> directly, so its own <c>Elsewhere</c> is the
/// implementation and the trap never fires. A stranger's project on the published 0.2.0 hit it on the first
/// attempt: a fixture BASE class implementing the interface, and a derived class declaring
/// <c>Elsewhere</c> — which compiles, reads as an override, and is never called
/// (<c>docs/DECISIONS.md</c> D39).</para>
///
/// <para>The consequence is the bad kind. Both deployment-isolation checks would then run against the
/// DEFAULT second deployment rather than the one the author wrote, so the suite passes while testing
/// something nobody asked for — worse than failing, because nothing prompts anyone to look.</para>
/// </summary>
public class ShadowedFixtureTests
{
    private const string Check = "The fixture's own answers are the ones being used";

    [Fact]
    public async Task A_fixture_that_implements_the_interface_ITSELF_is_fine()
    {
        // The ordinary shape, and the one every fixture in this repository uses.
        var result = await new UnitDriverContract(new Direct()).RunAsync(Check);

        Assert.True(result.Passed, result.Detail);
    }

    [Fact]
    public async Task A_DERIVED_class_declaring_Elsewhere_is_reported_rather_than_ignored()
    {
        var result = await new UnitDriverContract(new Derived()).RunAsync(Check);

        Assert.False(result.Passed);
        Assert.Contains("Elsewhere", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("derived", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_trap_is_REAL_and_this_is_what_it_looks_like()
    {
        // Pinning the language behaviour itself, so the check above is never "fixed" by someone who
        // assumes C# resolves this the way an abstract member would.
        IUnitDriverFixture derived = new Derived();

        Assert.Equal("declared-on-derived", ((Derived)derived).Elsewhere!.Prefix);
        Assert.Equal("contract-2", derived.Elsewhere!.Prefix);          // …the DEFAULT answered
    }

    [Fact]
    public async Task A_fixture_declaring_nothing_uses_the_default_and_passes()
    {
        var result = await new UnitDriverContract(new Base()).RunAsync(Check);

        Assert.True(result.Passed, result.Detail);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private class Base : IUnitDriverFixture
    {
        private readonly MemoryTarget _target = new();

        public IUnitDriver Driver => _target;

        public ProcedureUnit Unit { get; } = new("web", "Website");

        public DeploymentRequest Request { get; } =
            new("contract", new DeploymentArtifact(new Dictionary<string, string>()));

        public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
    }

    /// <summary>Declares it where the interface mapping was already fixed by <see cref="Base"/>.</summary>
    private sealed class Derived : Base
    {
        public DeploymentRequest? Elsewhere => Request with { Prefix = "declared-on-derived" };
    }

    /// <summary>Declares the interface itself, so its own member IS the implementation.</summary>
    private sealed class Direct : IUnitDriverFixture
    {
        private readonly MemoryTarget _target = new();

        public IUnitDriver Driver => _target;

        public ProcedureUnit Unit { get; } = new("web", "Website");

        public DeploymentRequest Request { get; } =
            new("contract", new DeploymentArtifact(new Dictionary<string, string>()));

        public DeploymentRequest? Elsewhere => Request with { Prefix = "declared-directly" };

        public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
    }
}

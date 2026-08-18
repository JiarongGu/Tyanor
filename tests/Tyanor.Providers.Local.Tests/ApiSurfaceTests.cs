using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// The local provider's public surface, held to a checked-in baseline beside its own tests.
///
/// <para>Deliberately per-assembly rather than one file for everything: a provider's API change should fail
/// the provider's test project, and a provider written OUTSIDE this repository can copy
/// <c>tests/Shared/ApiSurface.cs</c> and get exactly this. See it for what is rendered and what is not.</para>
/// </summary>
public class ApiSurfaceTests
{
    [Fact]
    public void The_public_surface_is_what_the_baseline_records() =>
        ApiSurface.MatchesBaseline(typeof(LocalTarget).Assembly);
}

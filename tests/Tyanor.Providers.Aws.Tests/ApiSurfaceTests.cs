using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The AWS provider's public surface, held to a checked-in baseline beside its own tests.
///
/// <para>This assembly has the most to gain from it: the phase table and the classifier are deliberately
/// <c>internal</c> and reached through <c>InternalsVisibleTo</c>, so a well-meant <c>public</c> on one of them
/// would compile, pass every test, and commit us to a shape that exists to be changed. See
/// <c>tests/Shared/ApiSurface.cs</c>.</para>
/// </summary>
public class ApiSurfaceTests
{
    [Fact]
    public void The_public_surface_is_what_the_baseline_records() =>
        ApiSurface.MatchesBaseline(typeof(AwsTarget).Assembly);
}

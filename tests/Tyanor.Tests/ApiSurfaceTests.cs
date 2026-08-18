using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The library's public surface, held to a checked-in baseline.
///
/// <para>Read <c>tests/Shared/ApiSurface.cs</c> for why this exists. The short version: from 0.1.0 the public
/// surface is a promise, and nothing else in this repository can see it change — the build succeeds, every
/// test passes, and a <c>public</c> that should have been <c>internal</c> ships forever. This makes the
/// surface a file, so changing it is a diff somebody reads.</para>
///
/// <para>It matters most for THIS assembly, which merged four packages into one (D26). Types that were public
/// only because a sibling assembly needed to see them no longer have that excuse, and the baseline is where
/// the argument for each one gets made out loud.</para>
/// </summary>
public class ApiSurfaceTests
{
    [Fact]
    public void The_public_surface_is_what_the_baseline_records() =>
        ApiSurface.MatchesBaseline(typeof(Procedure).Assembly);
}

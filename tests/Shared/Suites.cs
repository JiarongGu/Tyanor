using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests.Support;

/// <summary>
/// Turning a contract suite's check names into xUnit cases, so each one reports under its own name.
///
/// <para>Shared by every test project through <c>tests/Directory.Build.props</c> rather than copied into
/// each. It was written three times — once per test assembly — which is three chances to drift on the one
/// helper whose job is to make sure no check is silently skipped.</para>
///
/// <para>It cannot live in <c>Tyanor.Testing</c> itself: that package takes no test-framework dependency,
/// deliberately, so that the contract suites run under xUnit, NUnit, MSTest or a console app
/// (<c>docs/DECISIONS.md</c> D15). <see cref="TheoryData{T}"/> is xUnit's, so this is the xUnit adapter and
/// belongs on this side of the line.</para>
/// </summary>
internal static class Suites
{
    /// <summary>Every check in a suite, as one xUnit case each.</summary>
    /// <param name="suite">The suite. Its fixture is never touched, so <c>null!</c> is fine here.</param>
    public static TheoryData<string> Names(ContractSuite suite)
    {
        var data = new TheoryData<string>();
        foreach (var check in suite.Checks) data.Add(check);
        return data;
    }
}

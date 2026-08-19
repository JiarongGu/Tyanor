namespace Tyanor.Tests.Support;

/// <summary>
/// The <see cref="DeploymentRequest"/> a test wants when the request is not what it is testing.
///
/// <para>Six test files had written the identical two-line helper — one prefix, an empty artifact, no
/// options — which is the same "written three times before it was written once" that produced
/// <see cref="Suites"/>. Most tests here are about the ENGINE's decisions, so the request is scaffolding
/// they have to construct and never look at again.</para>
///
/// <para>A test whose subject IS the request keeps building its own, deliberately: naming the prefix and the
/// options inline is the point when they are what the assertion is about.</para>
/// </summary>
internal static class Requests
{
    /// <summary>An artifact carrying nothing — for a target that reads no parts.</summary>
    public static DeploymentArtifact Nothing => new(new Dictionary<string, string>());

    /// <summary>A request with no options, under the prefix these tests conventionally use.</summary>
    /// <param name="prefix">The deployment name base. The default is the one nearly every test uses.</param>
    public static DeploymentRequest Bare(string prefix = "acme") => new(prefix, Nothing);

    /// <summary>A request carrying options, which is what a PROVIDER's tests configure units through.</summary>
    /// <param name="options">The unit settings under test.</param>
    /// <param name="prefix">The deployment name base.</param>
    public static DeploymentRequest With(Dictionary<string, string> options, string prefix = "acme") =>
        new(prefix, Nothing, options);
}

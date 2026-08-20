using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The AWS TARGET held to the contract a target written anywhere else will be held to — offline.
///
/// <para><b>Ungated on purpose.</b> The questions this asks are about composition rather than about AWS:
/// does a sweep tolerate there being nothing to sweep, and does it survive being run again. Both are
/// satisfied by omission — a provider that never wrote the tolerant path passes every other test it has —
/// which is exactly the shape this repository keeps being bitten by, and exactly what a contract suite is
/// for. Whether S3 accepts the calls stays behind <c>TYANOR_LIVE_AWS</c>.</para>
///
/// <para>The fakes replay real S3 error codes and invent none: an absent bucket refuses with
/// <c>NoSuchBucket</c> and a full one refuses to be deleted with <c>BucketNotEmpty</c>, which is why "empty
/// it first" is a tested behaviour here rather than a hope. See <c>docs/DECISIONS.md</c> D23.</para>
/// </summary>
public class AwsTargetContractTests
{
    private static readonly DeploymentRequest Request =
        new("contract", new DeploymentArtifact(new Dictionary<string, string>()));

    public static TheoryData<string> Checks() => Suites.Names(new DeploymentTargetContract(null!, "", Request));

    [Theory]
    [MemberData(nameof(Checks))]
    public async Task The_aws_target_satisfies(string check)
    {
        using var target = new AwsTarget(
            new StatefulCloudFormation(), new FakeS3(), new FakeCloudFront(), new FakeSts(), "ap-southeast-2");

        await new DeploymentTargetContract(target, "site", Request).AssertAsync(check);
    }
}

using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The content driver held to the contract every driver is held to — offline, against an in-memory bucket.
///
/// <para><b>Why this one can run without a cloud when the STACK one cannot.</b> D23 draws the line at
/// whether a fake would have to encode a vendor's semantics. For CloudFormation it would: create makes it
/// <c>CREATE_COMPLETE</c> eventually, a rollback settles somewhere specific, and a fake asserting all that
/// is just this repository agreeing with itself. S3 has no such state machine — the only behaviour this
/// driver uses is that what you put is what you list, and a dictionary is not a *model* of that so much as
/// the thing itself.</para>
///
/// <para>The fake is faithful in the one way that matters here: it refuses to write to a bucket that does
/// not exist, exactly as S3 does. That refusal is what proved the driver's phase mapping was wrong.</para>
/// </summary>
public class ContentDriverContractTests
{
    public static TheoryData<string> Checks() => Suites.Names(new UnitDriverContract(null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task A_content_unit_satisfies(string check) =>
        new UnitDriverContract(new ContentFixture()).AssertAsync(check);

    /// <summary>
    /// A content unit pointed at a bucket that already exists — which is the only way one ever runs, because
    /// the bucket belongs to the stack declared before it.
    /// </summary>
    private sealed class ContentFixture : IUnitDriverFixture
    {
        private const string Bucket = "contract-bucket";

        private readonly FakeS3 _s3 = new();
        private readonly string _dir;

        public ContentFixture()
        {
            var cfn = new FakeCloudFormation();
            var stacks = new StackUnit(cfn, _s3, new AwsAccount(new FakeSts()), "ap-southeast-2",
                TimeSpan.FromMilliseconds(1));
            Driver = new ContentUnit(_s3, new FakeCloudFront(), stacks);

            _dir = Path.Combine(Path.GetTempPath(), "tyanor-cc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "index.html"), "<h1>hi</h1>");

            Request = new DeploymentRequest("contract",
                new DeploymentArtifact(new Dictionary<string, string> { ["site"] = _dir }),
                new Dictionary<string, string>
                {
                    ["web.kind"] = AwsOptions.ContentKind,
                    ["web.source"] = "site",
                    ["web.bucket"] = Bucket,
                });
        }

        public IUnitDriver Driver { get; }

        public ProcedureUnit Unit { get; } = new("web", "Website files");

        public DeploymentRequest Request { get; }

        /// <summary>
        /// Back to nothing deployed — which for this unit means an EMPTY bucket, not an absent one. The
        /// bucket is another unit's resource and outlives this one's teardown, so emptying it is exactly
        /// what <see cref="IUnitDriver.RemoveAsync"/> does.
        /// </summary>
        public Task ResetAsync(CancellationToken ct)
        {
            _s3.Buckets[Bucket] = [];              // the stack made it; this unit never does
            return Task.CompletedTask;
        }
    }
}

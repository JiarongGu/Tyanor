using Amazon.CloudFormation.Model;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The two things a stack's parameters could not say until D34: <b>what an earlier unit produced</b>, and
/// <b>what the staging bucket is called</b>.
///
/// <para>Both are values that only exist once the run is under way, and `parameter.*` is static text — so
/// the first blocked the domain unit before any consumer reached it, and the second made the first adopter
/// hard-code <c>{prefix}-deploy-{account}</c> and make their own <c>sts:GetCallerIdentity</c> call to fill
/// in a parameter value. A workaround is a missing feature that has already been paid for once.</para>
///
/// <para>Every check here is about WHICH request this provider builds, which is our logic. Whether
/// CloudFormation accepts it stays behind <c>TYANOR_LIVE_AWS</c> (D23).</para>
/// </summary>
public class StackParameterTests : IDisposable
{
    private static readonly ProcedureUnit Web = new("web", "Website");

    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyanor-sp-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly StatefulCloudFormation _cfn = new();

    private readonly FakeS3 _s3 = new();

    private readonly FakeSts _sts = new();

    private readonly StackUnit _unit;

    public StackParameterTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "web.template.json"), "{}");
        _unit = new StackUnit(_cfn, _s3, new AwsAccount(_sts), "ap-southeast-2", NoWait);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }

    private DeploymentRequest Request(Dictionary<string, string> extra)
    {
        var options = new Dictionary<string, string>
        {
            ["web.kind"] = AwsOptions.StackKind,
            ["web.template"] = "template",
        };
        foreach (var (k, v) in extra) options[k] = v;

        return new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string>
            {
                ["template"] = Path.Combine(_dir, "web.template.json"),
                ["lambda"] = _dir,
            }),
            options);
    }

    private UnitContext Context(DeploymentRequest request) => new(Web, request);

    /// <summary>The parameters the `web` stack was actually sent.</summary>
    private Dictionary<string, string> Sent() => _cfn.ParametersFor("mysite-web");

    // ── a value an earlier unit produced ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_parameter_can_come_from_an_earlier_units_output()
    {
        // The case that blocks the domain unit: an ACM certificate ARN exists only once the run has issued
        // it, and it has to be inside the CloudFront distribution before the web stack deploys.
        _cfn.Outputs["CertificateArn"] = "arn:aws:acm:us-east-1:123456789012:certificate/abc";
        await Producer();

        await _unit.CreateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.parameterFrom.CertificateArn"] = "domain:CertificateArn",
        })));

        Assert.Equal("arn:aws:acm:us-east-1:123456789012:certificate/abc", Sent()["CertificateArn"]);
    }

    [Fact]
    public async Task Static_and_resolved_parameters_arrive_together()
    {
        _cfn.Outputs["CertificateArn"] = "arn:cert";
        await Producer();

        await _unit.CreateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.parameter.MemorySize"] = "512",
            ["web.parameterFrom.CertificateArn"] = "domain:CertificateArn",
        })));

        var sent = Sent();
        Assert.Equal("512", sent["MemorySize"]);
        Assert.Equal("arn:cert", sent["CertificateArn"]);
    }

    [Fact]
    public async Task A_reference_to_a_unit_that_is_not_deployed_is_REFUSED_rather_than_dropped()
    {
        // Dropping it would fail inside CloudFormation, naming the parameter and not the unit that was
        // supposed to produce it — sending the operator to read a template instead of their procedure.
        var error = await Assert.ThrowsAsync<AwsConfigurationException>(() =>
            _unit.CreateAsync(Context(Request(new Dictionary<string, string>
            {
                ["web.parameterFrom.CertificateArn"] = "domain:CertificateArn",
            }))));

        Assert.Contains("domain:CertificateArn", error.Message);
        Assert.Contains("web", error.Message);
        Assert.Empty(_cfn.Created);              // …and nothing was issued
    }

    [Fact]
    public async Task A_reference_to_an_output_the_unit_does_not_export_is_REFUSED()
    {
        _cfn.Outputs["SomethingElse"] = "x";
        await Producer();

        var error = await Assert.ThrowsAsync<AwsConfigurationException>(() =>
            _unit.CreateAsync(Context(Request(new Dictionary<string, string>
            {
                ["web.parameterFrom.CertificateArn"] = "domain:CertificateArn",
            }))));

        Assert.Contains("output", error.Message);
    }

    [Fact]
    public async Task A_malformed_reference_is_caught_OFFLINE()
    {
        // Same parse the apply runs. A reference that reads as valid here and is refused at apply time
        // would be worse than not checking.
        var problems = await _unit.ValidateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.parameterFrom.CertificateArn"] = "no-colon-here",
        })));

        Assert.Contains(problems, p => p.Contains("parameterFrom.CertificateArn") && p.Contains("no-colon-here"));
    }

    [Fact]
    public async Task A_parameter_set_BOTH_ways_is_refused_rather_than_resolved_by_precedence()
    {
        // Which one an operator meant is not knowable, and picking one silently deploys a value nobody
        // wrote down.
        //
        // The producer is deployed and the output IS there, deliberately: without that, removing the
        // collision check leaves an unresolvable reference that throws the same exception type for a
        // different reason, and this passes while checking nothing. Mutation caught exactly that.
        _cfn.Outputs["CertificateArn"] = "arn:resolved";
        await Producer();

        var options = new Dictionary<string, string>
        {
            ["web.parameter.CertificateArn"] = "arn:written-by-hand",
            ["web.parameterFrom.CertificateArn"] = "domain:CertificateArn",
        };

        var problems = await _unit.ValidateAsync(Context(Request(options)));
        Assert.Contains(problems, p => p.Contains("both"));

        // …and again at apply time, because the two must not be able to disagree.
        var error = await Assert.ThrowsAsync<AwsConfigurationException>(
            () => _unit.CreateAsync(Context(Request(options))));

        Assert.Contains("both", error.Message);
    }

    // ── the staging bucket ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_staging_bucket_can_be_delivered_as_a_parameter()
    {
        // What the first adopter hard-coded, and made an sts:GetCallerIdentity call of their own to build.
        await _unit.CreateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.assets"] = "lambda",
            ["web.assetsBucketParameter"] = "AssetsBucketName",
        })));

        Assert.Equal("mysite-deploy-123456789012", Sent()["AssetsBucketName"]);
    }

    [Fact]
    public async Task It_is_the_bucket_the_assets_ACTUALLY_went_to()
    {
        // The point of passing it rather than documenting it: the value and the upload cannot disagree,
        // because they come from one call. A convention recomputed elsewhere can drift from this one.
        await _unit.CreateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.assets"] = "lambda",
            ["web.assetsBucketParameter"] = "AssetsBucketName",
        })));

        Assert.Equal(Sent()["AssetsBucketName"], Assert.Single(_s3.Created.Distinct()));
    }

    [Fact]
    public async Task Nothing_is_passed_unless_the_parameter_is_NAMED()
    {
        // CloudFormation refuses a parameter the template does not declare, so a bucket supplied helpfully
        // would break every template that did not ask for one.
        await _unit.CreateAsync(Context(Request(new Dictionary<string, string> { ["web.assets"] = "lambda" })));

        Assert.Empty(Sent());
    }

    [Fact]
    public async Task Naming_the_parameter_without_any_assets_is_reported_offline()
    {
        // Almost certainly a template that will not find its own Lambda code — cheap to say now, expensive
        // to discover from a rollback citing a bucket the operator never configured.
        var problems = await _unit.ValidateAsync(Context(Request(new Dictionary<string, string>
        {
            ["web.assetsBucketParameter"] = "AssetsBucketName",
        })));

        Assert.Contains(problems, p => p.Contains(AwsOptions.Assets));
    }

    /// <summary>Deploy the `domain` unit, so its outputs are readable.</summary>
    private Task Producer() =>
        new StackUnit(_cfn, _s3, new AwsAccount(_sts), "ap-southeast-2", NoWait)
            .CreateAsync(new UnitContext(new ProcedureUnit("domain", "Domain"),
                Request(new Dictionary<string, string>
                    { ["domain.kind"] = AwsOptions.StackKind, ["domain.template"] = "template" })));
}

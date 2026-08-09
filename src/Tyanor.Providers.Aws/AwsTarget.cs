using Amazon;
using Amazon.CloudFormation;
using Amazon.CloudFront;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecurityToken;

namespace Tyanor.Providers.Aws;

/// <summary>
/// Deploys to AWS: CloudFormation stacks, and a bucket of website files in front of them.
///
/// <code>
/// var procedure = new Procedure("site",
/// [
///     new ProcedureUnit("db",  "Database", Weight: 4),
///     new ProcedureUnit("api", "API",      Weight: 3),
///     new ProcedureUnit("web", "Website"),
///     new ProcedureUnit("content", "Website files"),
/// ]);
///
/// var request = new DeploymentRequest("mysite",
///     new DeploymentArtifact(new Dictionary&lt;string, string&gt;
///     {
///         ["db-template"] = "bundle/mysite-db.template.json",
///         ["api-template"] = "bundle/mysite-api.template.json",
///         ["web-template"] = "bundle/mysite-web.template.json",
///         ["lambda"] = "bundle/assets",
///         ["site"] = "dist/web",
///     }),
///     new Dictionary&lt;string, string&gt;
///     {
///         ["kind"] = "stack",                          // all but one unit
///         ["db.template"] = "db-template",
///         ["api.template"] = "api-template",  ["api.assets"] = "lambda",
///         ["web.template"] = "web-template",
///         ["content.kind"] = "content",                // the exception
///         ["content.source"] = "site",
///         ["content.bucketFrom"] = "web:webbucketname",
///         ["content.invalidateFrom"] = "web:distributionid",
///     });
///
/// var runner = new ProcedureRunner(new AwsTarget(credentials), history, state);
/// if ((await runner.PlanAsync(procedure, request)).Replacements.Count > 0) /* ask first */;
/// await runner.ApplyAsync(procedure, request, report);
/// </code>
///
/// <para><b>Templates are already synthesized.</b> This provider uploads and deploys them; it does not run
/// <c>cdk synth</c>, <c>helm template</c> or a compiler (<c>docs/DECISIONS.md</c> D5). That is what lets an
/// operator deploy with no cloud toolchain installed — the property the first consumer needs, because its
/// user is a non-technical owner running a desktop app with no Node and no CDK.</para>
///
/// <para><b>Ported, not written.</b> The reconcile branches, the retry, the classification and the
/// pause/fail decision that used to live beside these calls are all in the engine now, and the error codes in
/// <see cref="AwsFailureClassifier"/> are the ones a real deployment hit rather than a list someone
/// reasoned out.</para>
/// </summary>
public sealed class AwsTarget : IDeploymentTarget, IDisposable
{
    private readonly AmazonCloudFormationClient _cfn;
    private readonly AmazonS3Client _s3;
    private readonly AmazonSecurityTokenServiceClient _sts;
    private readonly AmazonCloudFrontClient _cloudFront;
    private readonly AwsAccount _account;

    /// <summary>
    /// Build a target for one account and region.
    /// </summary>
    /// <param name="credentials">
    /// Key, secret and <see cref="TargetCredentials.Region"/>. The region is required: every AWS call needs
    /// one, and defaulting it would deploy somewhere the operator did not name.
    /// </param>
    /// <param name="custom">
    /// Units this application brings of its own — verify a migration applied, warm a cache, call a health
    /// endpoint that means something only to you. They go in the same procedure as the stacks and get the same
    /// plan, resume and classification. See <see cref="CustomUnits"/>.
    /// </param>
    /// <exception cref="ArgumentException">No region, or one AWS does not recognise.</exception>
    public AwsTarget(TargetCredentials credentials, CustomUnits? custom = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Region))
            throw new ArgumentException("An AWS region is required.", nameof(credentials));

        Region = RegionEndpoint.GetBySystemName(credentials.Region);
        var basic = new BasicAWSCredentials(credentials.KeyId.Trim(), credentials.Secret.Trim());

        _cfn = new AmazonCloudFormationClient(basic, Region);
        _s3 = new AmazonS3Client(basic, Region);
        _sts = new AmazonSecurityTokenServiceClient(basic, Region);
        // CloudFront is global and its API only answers in us-east-1, whatever region everything else is in.
        _cloudFront = new AmazonCloudFrontClient(basic, RegionEndpoint.USEast1);

        _account = new AwsAccount(_sts);
        Driver = new AwsUnitDriver(_cfn, _s3, _cloudFront, _account, Region.SystemName, custom);
        Classifier = FailureClassifiers.Chain(new AwsFailureClassifier(), custom?.Classifier);
    }

    /// <inheritdoc/>
    public string Id => "aws";

    /// <summary>The region every non-global call goes to.</summary>
    public RegionEndpoint Region { get; }

    /// <inheritdoc/>
    public IUnitDriver Driver { get; }

    /// <inheritdoc/>
    public IFailureClassifier Classifier { get; }

    /// <summary>
    /// Exercise the credentials and report the account and identity.
    /// </summary>
    /// <param name="credentials">
    /// Ignored — this target was built with the credentials it uses, and rebuilding its clients per call
    /// would defeat the SDK's connection reuse. Pass null.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// A real call (<c>GetCallerIdentity</c>), because "the fields are filled in" is not validation. The
    /// account it returns is the cheapest guard there is against deploying into the wrong one — which is a
    /// mistake that is trivial to prevent beforehand and expensive to unpick afterwards.
    /// </remarks>
    public async Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct)
    {
        try
        {
            var who = await _account.WhoAsync(ct);
            return new TargetIdentity(true, who.Account, who.Arn);
        }
        catch (Exception e) when (Classifier.Classify(e) is FailureClass.Credentials)
        {
            return new TargetIdentity(false, Error: "AWS rejected these credentials: " + e.Message);
        }
        catch (AmazonServiceException e)
        {
            // A transient failure here is NOT "these credentials are bad" — saying so would send an operator
            // to re-enter keys that were fine.
            return new TargetIdentity(false, Error: "Could not reach AWS to check these credentials: " + e.Message);
        }
    }

    /// <summary>Release the SDK clients.</summary>
    public void Dispose()
    {
        _cfn.Dispose();
        _s3.Dispose();
        _sts.Dispose();
        _cloudFront.Dispose();
    }
}

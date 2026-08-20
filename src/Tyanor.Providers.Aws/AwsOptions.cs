namespace Tyanor.Providers.Aws;

/// <summary>
/// The settings this provider reads out of <see cref="DeploymentRequest.Options"/>, named in one place so
/// they can be documented once and misspelled never.
///
/// <para>Read with <see cref="DeploymentRequest.Option(string, string)"/>, so each can be written per unit
/// (<c>"web.kind"</c>) or once for the procedure (<c>"kind"</c>).</para>
/// </summary>
public static class AwsOptions
{
    /// <summary>What this unit IS — <see cref="StackKind"/> or <see cref="ContentKind"/>. Required per unit.</summary>
    public const string Kind = "kind";

    /// <summary>A CloudFormation stack. The unit that has a real control plane behind it.</summary>
    public const string StackKind = "stack";

    /// <summary>Files synced to an S3 bucket, optionally invalidating a CloudFront distribution.</summary>
    public const string ContentKind = "content";

    /// <summary>
    /// The artifact part naming this stack's template FILE — already synthesized. Tyanor does not run
    /// <c>cdk synth</c>; see <c>docs/DECISIONS.md</c> D5.
    /// </summary>
    public const string Template = "template";

    /// <summary>
    /// Optional artifact part naming a DIRECTORY of files the template expects in the assets bucket — Lambda
    /// zips and the like. Uploaded by their file names, which is the object key the template refers to.
    /// </summary>
    public const string Assets = "assets";

    /// <summary>
    /// Prefix for CloudFormation parameters, read with
    /// <see cref="DeploymentRequest.OptionSet"/>: <c>"api.parameter.MemorySize" = "512"</c>.
    /// </summary>
    public const string ParameterPrefix = "parameter";

    /// <summary>
    /// Prefix for CloudFormation parameters whose value an EARLIER unit produced:
    /// <c>"web.parameterFrom.CertificateArn" = "domain:CertificateArn"</c>, naming a unit and one of its
    /// outputs.
    /// </summary>
    /// <remarks>
    /// <para>The same <c>"{unit}:{OutputKey}"</c> reference <see cref="BucketFrom"/> takes, and the same
    /// reason: some values do not exist until the run is under way — an issued certificate's ARN, a
    /// generated endpoint — so a static <see cref="ParameterPrefix"/> value cannot carry them. Resolved
    /// when the run reaches this unit, which is what lets ordering express the dependency without an edge.</para>
    /// <para>A parameter named in BOTH groups is refused rather than resolved by precedence: which one an
    /// operator meant is not knowable, and picking one silently is how a deployment gets a value nobody
    /// wrote down.</para>
    /// </remarks>
    public const string ParameterFromPrefix = "parameterFrom";

    /// <summary>
    /// The name of a CloudFormation parameter to fill with the STAGING BUCKET this provider uploaded
    /// <see cref="Assets"/> to: <c>"api.assetsBucketParameter" = "AssetsBucketName"</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>For a synthesized template that must NAME the bucket its assets are in.</b> A CDK-style
    /// template refers to its Lambda code by bucket and key, and the bucket is Tyanor's rather than the
    /// template's — so without this the only way to fill that parameter is to recompute
    /// <c>{prefix}-deploy-{account}</c> in the composition root and make an <c>sts:GetCallerIdentity</c>
    /// call to learn the account. That was the first real workaround adoption produced, and it left a
    /// consumer depending on a convention this provider owns and could move (<c>docs/DECISIONS.md</c> D34).</para>
    /// <para>Nothing is passed unless you name the parameter, because CloudFormation refuses a parameter
    /// the template does not declare — so a value supplied helpfully would break every template that did
    /// not want it.</para>
    /// <para>The other things a template might want to know are already CloudFormation's own:
    /// <c>AWS::Region</c> and <c>AWS::AccountId</c> are pseudo-parameters. The staging bucket is the one
    /// fact about a deployment that only Tyanor knows, which is why this is one setting and not a family.</para>
    /// </remarks>
    public const string AssetsBucketParameter = "assetsBucketParameter";

    /// <summary>
    /// Comma-separated stack capabilities. Defaults to <see cref="DefaultCapabilities"/> — anything creating
    /// an IAM role needs them, which in practice is every stack with compute in it.
    /// </summary>
    public const string Capabilities = "capabilities";

    /// <summary>The capabilities assumed when none are named.</summary>
    public const string DefaultCapabilities = "CAPABILITY_IAM,CAPABILITY_NAMED_IAM";

    /// <summary>The artifact part naming a DIRECTORY to sync, for a content unit.</summary>
    public const string Source = "source";

    /// <summary>The destination bucket, when it is known up front rather than produced by a stack.</summary>
    public const string Bucket = "bucket";

    /// <summary>
    /// Where to READ the destination bucket from: <c>"{unit}:{OutputKey}"</c>, naming a stack unit and one
    /// of its CloudFormation outputs.
    /// </summary>
    /// <remarks>
    /// The cross-unit reference is expressed in configuration and resolved at apply time, which is what lets
    /// ordering carry the dependency: the stack that produces the bucket is declared before the unit that
    /// fills it, exactly as <c>units-not-graphs.md</c> asks. No edge, no graph.
    /// </remarks>
    public const string BucketFrom = "bucketFrom";

    /// <summary>
    /// Optional <c>"{unit}:{OutputKey}"</c> naming the CloudFront distribution id to invalidate after a
    /// sync. Without it the files are uploaded and the CDN keeps serving the old ones until they expire.
    /// </summary>
    public const string InvalidateFrom = "invalidateFrom";
}

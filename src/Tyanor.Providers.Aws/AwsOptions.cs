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

using Amazon.CloudFormation;
using Amazon.CloudFront;
using Amazon.S3;

namespace Tyanor.Providers.Aws;

/// <summary>
/// The two kinds of AWS thing this provider deploys, and the pair is the whole of what the first consumer's
/// site needs: CloudFormation stacks for the infrastructure, and a bucket of files for the website — which is
/// not a CloudFormation asset and therefore cannot be one of the stacks.
///
/// <para>The dispatch belongs to <see cref="UnitKindDriver"/>. There is no orchestration here, and the
/// temptation to add it is real: the code this was ported from had ordering, reconcile, retry and the
/// pause/fail decision inside a single method beside the SDK calls, and pulling them out is most of why this
/// file is four lines long.</para>
/// </summary>
internal sealed class AwsUnitDriver : UnitKindDriver
{
    /// <summary>Build the driver for one account and region.</summary>
    /// <param name="cfn">CloudFormation.</param>
    /// <param name="s3">S3 — for staging templates and for website content.</param>
    /// <param name="cloudFront">CloudFront, which only answers in us-east-1.</param>
    /// <param name="account">The account, asked once, because the staging bucket is named after it.</param>
    /// <param name="region">The region every non-global call goes to.</param>
    public AwsUnitDriver(
        IAmazonCloudFormation cfn, IAmazonS3 s3, IAmazonCloudFront cloudFront, AwsAccount account, string region)
        : base(AwsOptions.Kind)
    {
        // The content unit reads the bucket name out of a stack's outputs, so it needs the stack driver. Not
        // a cycle: stacks know nothing about content.
        var stacks = new StackUnit(cfn, s3, account, region);
        Register(AwsOptions.StackKind, stacks);
        Register(AwsOptions.ContentKind, new ContentUnit(s3, cloudFront, stacks));
    }
}

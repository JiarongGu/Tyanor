using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;

namespace Tyanor.Providers.Aws;

/// <summary>
/// A unit of this provider is configured wrongly — no template named, a <c>bucketFrom</c> that does not
/// parse, a part that is not in the artifact.
///
/// <para>Separate from <see cref="AwsDeploymentException"/> on purpose. Both end a run, but "you have
/// configured this wrongly, and nothing has been touched" and "CloudFormation rolled your stack back" are
/// different situations for whoever is reading — see <see cref="DefinitionException"/>.</para>
/// </summary>
/// <param name="message">Plain language, naming what was expected.</param>
public sealed class AwsConfigurationException(string message) : DefinitionException(message);

/// <summary>
/// AWS did something terminal that this provider can describe better than the raw exception can — a stack
/// that settled into a rollback, a delete that failed.
///
/// <para>Carries no <see cref="FailureClass"/> because it is always hard: the template produced this, and
/// issuing the same template again produces it again. <see cref="AwsFailureClassifier"/> returns null for it
/// and the engine's default for null is <see cref="FailureClass.Hard"/>, which is the right answer.</para>
/// </summary>
/// <param name="message">Plain language, including CloudFormation's first failure reason.</param>
public sealed class AwsDeploymentException(string message) : Exception(message);

/// <summary>
/// How AWS's failures map onto the three classes.
///
/// <para><b>Ported code, kept code.</b> Every error code below is one a real deployment actually hit, in an
/// application a non-technical owner runs unattended. None of them was reasoned into the list — which is
/// exactly why none should be removed for looking redundant. Two of the credential codes differ only in
/// which service emitted them.</para>
/// </summary>
internal sealed class AwsFailureClassifier : IFailureClassifier
{
    /// <summary>
    /// Codes meaning "AWS rejected who we are". A pause, never a failure: everything provisioned so far is
    /// intact, and the operator re-authenticates and resumes.
    /// </summary>
    private static readonly HashSet<string> CredentialCodes =
    [
        "ExpiredToken", "ExpiredTokenException", "InvalidClientTokenId", "UnrecognizedClientException",
        "RequestExpired", "InvalidAccessKeyId", "SignatureDoesNotMatch", "AuthFailure",
        "TokenRefreshRequired", "InvalidSecurityToken", "InvalidSecurity",
    ];

    /// <summary>
    /// Codes meaning "AWS was busy or briefly broken". Nothing about the desired state is wrong, so these
    /// are retried and then paused.
    /// </summary>
    private static readonly HashSet<string> TransientCodes =
    [
        "Throttling", "ThrottlingException", "RequestLimitExceeded", "RequestTimeout",
        "RequestTimeoutException", "ServiceUnavailable", "InternalFailure", "InternalError",
        "PriorRequestNotComplete",
    ];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>The whole chain, via the framework's walk. The AWS SDK wraps its own exceptions, and a
    /// task-based call path adds an <see cref="AggregateException"/> on top — a classifier reading only the
    /// outermost calls an expired token a hard failure and throws away a deployment that was entirely
    /// fine.</para>
    /// <para>Anything this does not recognise returns null, and the engine treats that as
    /// <see cref="FailureClass.Hard"/> — the safe default, and where a malformed template, a quota needing a
    /// human and an unverified account all correctly land, because no amount of retrying or
    /// re-authenticating resolves any of them.</para>
    /// </remarks>
    public FailureClass? Classify(Exception error) => FailureClassifiers.Walk(error, Single);

    /// <summary>One exception, read on its codes. Null means "not mine" — see the walk above.</summary>
    private static FailureClass? Single(Exception e)
    {
        if (e is AmazonServiceException aws)
        {
            var code = aws.ErrorCode ?? "";
            if (CredentialCodes.Contains(code)) return FailureClass.Credentials;

            // Retryable is the SDK's own verdict and it is checked first, because it knows about codes
            // this list has never seen.
            if (aws.Retryable is not null
                || TransientCodes.Contains(code)
                || (int)aws.StatusCode >= 500
                || aws.StatusCode == HttpStatusCode.TooManyRequests)
                return FailureClass.Transient;
        }

        // Below the SDK: the network itself. Not AWS refusing, just not arriving.
        return e is HttpRequestException or SocketException or TimeoutException ? FailureClass.Transient : null;
    }
}

using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;

namespace Tyanor.Providers.Aws;

/// <summary>
/// How AWS's failures map onto the three classes.
///
/// <para><b>Ported code, kept code.</b> Every error code below is one a real deployment actually hit, in an
/// application a non-technical owner runs unattended. None of them was reasoned into the list — which is
/// exactly why none should be removed for looking redundant. Two of the credential codes differ only in
/// which service emitted them.</para>
/// </summary>
public sealed class AwsFailureClassifier : IFailureClassifier
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
    public FailureClass? Classify(Exception error)
    {
        // The whole chain. The AWS SDK wraps its own exceptions, and a task-based call path adds an
        // AggregateException on top — a classifier reading only the outermost calls an expired token a hard
        // failure and throws away a deployment that was entirely fine.
        for (Exception? e = error; e is not null; e = e.InnerException)
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
            if (e is HttpRequestException or SocketException or TimeoutException) return FailureClass.Transient;
        }

        // Not ours. The engine treats that as Hard, which is the safe default — and it is where a malformed
        // template, a quota needing a human, and an unverified account all correctly land, because no amount
        // of retrying or re-authenticating resolves any of them.
        return null;
    }
}

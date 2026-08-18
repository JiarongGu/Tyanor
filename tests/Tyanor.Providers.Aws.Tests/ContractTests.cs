using System.Net;
using System.Net.Sockets;
using Amazon.CloudFormation;
using Amazon.Runtime;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The AWS classifier held to the contract a classifier written anywhere else will be held to.
///
/// <para>The suite adds what a hand-written table of codes cannot: it wraps every sample one and two levels
/// deep and insists the answer does not change. That is the failure this whole class of code has — SDKs wrap
/// the informative exception inside a generic one, and a classifier reading only the outermost calls an
/// expired token a hard failure and throws away a deployment that only needed re-authenticating.</para>
/// </summary>
public class AwsClassifierContractTests
{
    private static readonly FailureClassifierContract Suite = new(new AwsFixture());

    public static TheoryData<string> Checks() => Suites.Names(Suite);

    [Theory]
    [MemberData(nameof(Checks))]
    public Task It_satisfies(string check) => Suite.AssertAsync(check);

    private sealed class AwsFixture : IFailureClassifierFixture
    {
        private static AmazonServiceException Aws(string code, HttpStatusCode status = HttpStatusCode.BadRequest) =>
            new("AWS said no") { ErrorCode = code, StatusCode = status };

        public IFailureClassifier Classifier { get; } = new AwsFailureClassifier();

        // Every one of these is a code a real deployment hit, in an application a non-technical owner runs.
        public IReadOnlyList<Exception> CredentialErrors { get; } =
        [
            Aws("ExpiredToken"),
            Aws("InvalidClientTokenId"),
            Aws("SignatureDoesNotMatch"),
            Aws("TokenRefreshRequired"),
        ];

        public IReadOnlyList<Exception> TransientErrors { get; } =
        [
            Aws("Throttling"),
            Aws("ServiceUnavailable"),
            Aws("SomethingNew", HttpStatusCode.InternalServerError),
            Aws("SomethingElse", HttpStatusCode.TooManyRequests),
            new HttpRequestException("connection reset"),
            new SocketException(),
        ];

        public IReadOnlyList<Exception> HardErrors { get; } =
        [
            new AmazonCloudFormationException("Template format error: unsupported structure")
            { ErrorCode = "ValidationError", StatusCode = HttpStatusCode.BadRequest },
            Aws("LimitExceededException"),                          // a quota needing a human
            Aws("OptInRequired"),                                   // a region not enabled
            new AwsConfigurationException("names no template"),
            new AwsDeploymentException("api failed (ROLLBACK_COMPLETE)"),
        ];
    }
}

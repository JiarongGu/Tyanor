using System.Net;
using System.Net.Sockets;
using Amazon.CloudFormation;
using Amazon.Runtime;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The classifier, against the real AWS error codes.
///
/// <para>Every code here is one a real deployment actually hit, in an application a non-technical owner runs
/// unattended. That is why this test names them literally: a mocked SDK cannot catch a status code spelled
/// wrong, and both ways of being wrong are expensive. A credential error read as hard throws away a
/// deployment that was entirely fine; a hard error read as transient tells the same lie five times and then
/// reports the wrong thing anyway.</para>
/// </summary>
public class AwsFailureClassifierTests
{
    private static readonly IFailureClassifier Classifier = new AwsFailureClassifier();

    private static AmazonServiceException Aws(string code, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new("AWS said no") { ErrorCode = code, StatusCode = status };

    [Theory]
    [InlineData("ExpiredToken")]
    [InlineData("ExpiredTokenException")]
    [InlineData("InvalidClientTokenId")]
    [InlineData("UnrecognizedClientException")]
    [InlineData("RequestExpired")]
    [InlineData("InvalidAccessKeyId")]
    [InlineData("SignatureDoesNotMatch")]
    [InlineData("AuthFailure")]
    [InlineData("TokenRefreshRequired")]
    [InlineData("InvalidSecurityToken")]
    [InlineData("InvalidSecurity")]
    public void Every_credential_code_PAUSES_the_run(string code)
        // The defining behaviour. A tool that fails here discards twenty minutes of correct provisioning and
        // teaches its users to fear running it again.
        => Assert.Equal(FailureClass.Credentials, Classifier.Classify(Aws(code)));

    [Theory]
    [InlineData("Throttling")]
    [InlineData("ThrottlingException")]
    [InlineData("RequestLimitExceeded")]
    [InlineData("RequestTimeout")]
    [InlineData("RequestTimeoutException")]
    [InlineData("ServiceUnavailable")]
    [InlineData("InternalFailure")]
    [InlineData("InternalError")]
    [InlineData("PriorRequestNotComplete")]
    public void Every_transient_code_is_retried(string code)
        => Assert.Equal(FailureClass.Transient, Classifier.Classify(Aws(code)));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void A_server_side_status_is_transient_whatever_the_code_says(HttpStatusCode status)
        // A 5xx that never stops is still transient: it pauses after the retry budget rather than failing,
        // because nothing about the desired state is wrong.
        => Assert.Equal(FailureClass.Transient, Classifier.Classify(Aws("SomethingNew", status)));

    [Fact]
    public void An_unknown_code_with_a_client_side_status_is_NOT_guessed_as_transient()
    {
        // The classifier also honours AmazonServiceException.Retryable — the SDK's own verdict, which knows
        // about codes this list has never seen. That clause has no test here and deliberately so: the
        // property is set by the SDK internally and has no public setter in v4, so any test of it would be
        // testing a fabrication. It is exercised by the live test.
        //
        // What IS pinned here is the surrounding behaviour: with no Retryable, no known code and a 4xx,
        // nothing is assumed.
        Assert.Null(Classifier.Classify(Aws("AnEntirelyNewCode", HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void The_network_underneath_the_SDK_is_transient_too()
    {
        // Not AWS refusing — just not arriving.
        Assert.Equal(FailureClass.Transient, Classifier.Classify(new HttpRequestException("reset")));
        Assert.Equal(FailureClass.Transient, Classifier.Classify(new SocketException()));
        Assert.Equal(FailureClass.Transient, Classifier.Classify(new TimeoutException()));
    }

    [Fact]
    public void A_malformed_template_is_not_recognised_so_the_engine_fails_the_run()
    {
        // "Not mine" and "harmless" are different answers. Null means Hard to the engine, which is right:
        // retrying a template CloudFormation has already rejected tells the same lie five times.
        Assert.Null(Classifier.Classify(
            new AmazonCloudFormationException("Template format error: unsupported structure")
            { ErrorCode = "ValidationError", StatusCode = HttpStatusCode.BadRequest }));

        Assert.Null(Classifier.Classify(new InvalidOperationException("api failed (ROLLBACK_COMPLETE)")));
    }

    [Fact]
    public void An_account_level_gate_is_HARD_even_though_it_feels_temporary()
    {
        // A quota, an unverified account, a region not enabled. No amount of retrying resolves any of them,
        // and looping hides the one message that would tell the operator what to do.
        Assert.Null(Classifier.Classify(Aws("LimitExceededException")));
        Assert.Null(Classifier.Classify(Aws("OptInRequired")));
    }

    [Fact]
    public void A_credential_error_wrapped_in_a_generic_one_still_classifies()
    {
        // The most common way a classifier goes quietly wrong. The AWS SDK wraps its own exceptions and a
        // task-based call path adds an AggregateException on top; reading only the outermost calls an expired
        // token a hard failure.
        var wrapped = new InvalidOperationException("deploying the API stack",
            new AggregateException(Aws("ExpiredToken")));

        Assert.Equal(FailureClass.Credentials, Classifier.Classify(wrapped));
    }

    [Fact]
    public void Credentials_win_over_transient_when_an_error_could_be_read_as_both()
    {
        // An expired token arriving with a 500 must not be retried five times before anyone is told to
        // re-authenticate — the operator's action is different, and it is the only one that helps.
        Assert.Equal(FailureClass.Credentials,
            Classifier.Classify(Aws("ExpiredToken", HttpStatusCode.InternalServerError)));
    }
}

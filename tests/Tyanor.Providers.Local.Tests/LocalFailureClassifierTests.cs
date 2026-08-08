using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// The classifier, tested directly and against the REAL codes.
///
/// <para>This is the part of a provider most worth testing without the provider: a mocked filesystem
/// cannot catch a status code spelled wrong, and every way this goes wrong goes wrong quietly. A
/// credential failure mis-read as hard throws away a correct deployment; a hard failure mis-read as
/// transient retries a lie five times and then reports the wrong thing anyway.</para>
/// </summary>
public class LocalFailureClassifierTests
{
    private static readonly IFailureClassifier Classifier = new LocalTarget("unused").Classifier;

    [Fact]
    public void An_access_denial_is_a_CREDENTIAL_failure_even_though_a_machine_has_no_credentials()
    {
        // The insight this provider produced. There is no token here, but the OS has still rejected who we
        // are, and the operator's next move is the credential move: become someone allowed to do this and
        // resume — with everything already deployed still intact.
        Assert.Equal(FailureClass.Credentials, Classifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(FailureClass.Credentials, Classifier.Classify(new SecurityException()));
    }

    [Theory]
    [InlineData(5)]     // ERROR_ACCESS_DENIED
    [InlineData(13)]    // EACCES
    public void The_access_denied_codes_from_both_operating_systems_are_credentials(int code)
        => Assert.Equal(FailureClass.Credentials, Classifier.Classify(new Win32Exception(code)));

    [Theory]
    [InlineData(2)]     // ERROR_FILE_NOT_FOUND / ENOENT — the command does not exist
    [InlineData(3)]     // ERROR_PATH_NOT_FOUND
    public void A_command_that_is_not_there_is_HARD(int code)
        => Assert.Equal(FailureClass.Hard, Classifier.Classify(new Win32Exception(code)));

    [Theory]
    [InlineData(unchecked((int)0x80070020))]    // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021))]    // ERROR_LOCK_VIOLATION
    public void A_file_another_process_is_holding_is_TRANSIENT(int hresult)
        // The commonest way a redeploy fails on Windows, and it clears on its own — which is the whole
        // definition of transient. Classifying it hard would make every redeploy a coin toss.
        => Assert.Equal(FailureClass.Transient, Classifier.Classify(new IOException("locked", hresult)));

    [Fact]
    public void A_port_still_held_by_the_process_we_just_stopped_is_TRANSIENT()
        => Assert.Equal(FailureClass.Transient,
            Classifier.Classify(new SocketException((int)SocketError.AddressAlreadyInUse)));

    [Fact]
    public void A_missing_file_is_HARD_and_a_missing_directory_too()
    {
        Assert.Equal(FailureClass.Hard, Classifier.Classify(new FileNotFoundException()));
        Assert.Equal(FailureClass.Hard, Classifier.Classify(new DirectoryNotFoundException()));
    }

    [Fact]
    public void An_error_it_does_not_recognise_returns_null_so_the_engine_treats_it_as_hard()
        // "Not mine" and "harmless" are different answers. The engine's default for null is Hard, which is
        // the safe one: the error nobody anticipated is exactly the one not to retry silently.
        => Assert.Null(Classifier.Classify(new InvalidOperationException("something new")));

    [Fact]
    public void A_credential_error_wrapped_in_a_generic_one_still_classifies()
    {
        // The most common way a classifier goes quietly wrong. .NET wraps freely — Process.Start puts a
        // Win32Exception inside whatever it likes — and reading only the outermost calls every one of
        // them hard.
        var wrapped = new InvalidOperationException("could not start",
            new AggregateException(new Win32Exception(5)));

        Assert.Equal(FailureClass.Credentials, Classifier.Classify(wrapped));
    }

    [Fact]
    public void An_error_the_driver_raised_itself_carries_its_own_class()
    {
        // The driver knows exactly what happened; rediscovering it from an exception type would throw away
        // information we already had.
        Assert.Equal(FailureClass.Hard,
            Classifier.Classify(LocalDeploymentException.Misconfigured("web", "no source")));
        Assert.Equal(FailureClass.Transient,
            Classifier.Classify(LocalDeploymentException.Transient("web", "still starting")));
    }

    [Fact]
    public void The_class_survives_being_wrapped_too()
        => Assert.Equal(FailureClass.Transient, Classifier.Classify(
            new InvalidOperationException("wrapped", LocalDeploymentException.Transient("web", "slow"))));
}

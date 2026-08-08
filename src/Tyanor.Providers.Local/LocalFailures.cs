using System.ComponentModel;
using System.Net.Sockets;
using System.Security;

namespace Tyanor.Providers.Local;

/// <summary>
/// A unit of this provider is configured wrongly — no command, no source part, a port that is not a number.
///
/// <para>Separate from <see cref="LocalDeploymentException"/> on purpose. Both end a run, but a consumer
/// showing a deployment to a person needs to tell "you have configured this wrongly, fix it and nothing is
/// lost" from "the server would not start" — they read differently and only one is worth a support
/// conversation. That is what <see cref="DefinitionException"/> is for.</para>
/// </summary>
/// <param name="unit">The unit whose configuration is wrong.</param>
/// <param name="message">Plain language, naming what was expected.</param>
public sealed class LocalConfigurationException(string unit, string message) : DefinitionException(message)
{
    /// <summary>The unit whose configuration is wrong.</summary>
    public string Unit { get; } = unit;
}

/// <summary>
/// Something went wrong ON the machine, carrying the class the operator's next move depends on. Raised where
/// the driver already has the answer — a health check that timed out is not a mystery, and rediscovering that
/// from an exception type would lose information we had.
/// </summary>
/// <param name="unit">The unit it happened to.</param>
/// <param name="message">Plain language, for the operator.</param>
/// <param name="failure">Credentials, transient, or hard — see <see cref="FailureClass"/>.</param>
public sealed class LocalDeploymentException(string unit, string message, FailureClass failure)
    : Exception(message)
{
    /// <summary>The unit that failed.</summary>
    public string Unit { get; } = unit;

    /// <summary>What the operator should do next, in the only three flavours there are.</summary>
    public FailureClass Failure { get; } = failure;

    /// <summary>Terminal: the process would not run, or is not there to be waited on. Retrying repeats it.</summary>
    internal static LocalDeploymentException Hard(string unit, string message) =>
        new(unit, message, FailureClass.Hard);

    /// <summary>Nothing about the desired state is wrong; the machine was busy or slow. Bounded retry,
    /// then a pause the operator can resume from.</summary>
    internal static LocalDeploymentException Transient(string unit, string message) =>
        new(unit, message, FailureClass.Transient);
}

/// <summary>
/// How this machine's failures map onto the three classes.
///
/// <para><b>The interesting one is <see cref="FailureClass.Credentials"/>, because at first glance a local
/// target has none.</b> It does: the OS decides whether this identity may write to that directory or start
/// that process, and when it refuses, the operator's next move is exactly the credential move —
/// <i>be someone allowed to do this, then resume; the work so far is kept</i>. An access denial is the
/// provider rejecting who we are. That the class was named for expired cloud tokens is an accident of
/// which provider came first.</para>
///
/// <para>Classification is on <b>codes</b> — <see cref="Win32Exception.NativeErrorCode"/>,
/// <see cref="SocketError"/>, HRESULTs — never on message text, which is localized and not API surface.</para>
/// </summary>
internal sealed class LocalFailureClassifier : IFailureClassifier
{
    // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION, as they arrive on IOException.HResult. A file held
    // by the process we are about to replace is the single most common way a redeploy fails on Windows,
    // and it clears on its own — which is the definition of transient.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    // Win32 error codes AND the errno values .NET surfaces through the same property on Unix, because
    // "the command is not there" and "you may not run it" are the same two answers on both.
    private const int FileNotFound = 2;      // ERROR_FILE_NOT_FOUND / ENOENT
    private const int PathNotFound = 3;      // ERROR_PATH_NOT_FOUND
    private const int AccessDenied = 5;      // ERROR_ACCESS_DENIED
    private const int PermissionDenied = 13; // EACCES

    /// <inheritdoc/>
    public FailureClass? Classify(Exception error)
    {
        // Walk the WHOLE chain. .NET wraps freely — a Win32Exception from Process.Start arrives inside an
        // InvalidOperationException often enough that reading only the outermost would call every one of
        // them hard, which is how a classifier goes quietly wrong.
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            var known = Single(e);
            if (known is not null) return known;
        }
        return null;    // unrecognised — the engine treats it as Hard, which is the safe default
    }

    private static FailureClass? Single(Exception e) => e switch
    {
        LocalDeploymentException local => local.Failure,

        // The OS refused this identity. Same answer, same operator move, as an expired token.
        UnauthorizedAccessException or SecurityException => FailureClass.Credentials,
        Win32Exception w when w.NativeErrorCode is AccessDenied or PermissionDenied => FailureClass.Credentials,

        // Busy, not wrong.
        IOException io when io.HResult is SharingViolation or LockViolation => FailureClass.Transient,
        SocketException s when s.SocketErrorCode is SocketError.AddressAlreadyInUse
            or SocketError.TimedOut or SocketError.ConnectionRefused => FailureClass.Transient,
        TimeoutException => FailureClass.Transient,

        // The definition names something that is not there. No amount of waiting produces it.
        FileNotFoundException or DirectoryNotFoundException => FailureClass.Hard,
        Win32Exception w when w.NativeErrorCode is FileNotFound or PathNotFound => FailureClass.Hard,

        _ => null,
    };
}

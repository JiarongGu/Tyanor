using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace Tyanor.Providers.Aws;

/// <summary>
/// The account we are deploying into, asked once.
///
/// <para>Needed because the staging bucket is named after it, and worth memoizing because a procedure with
/// three stacks would otherwise call STS six times to compute the same string. Also the answer
/// <see cref="AwsTarget.ValidateAsync"/> shows the operator before anything starts — deploying into the
/// wrong account is the mistake that is cheapest to prevent and most expensive to undo.</para>
/// </summary>
internal sealed class AwsAccount(IAmazonSecurityTokenService sts)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CallerIdentity? _identity;

    /// <summary>The 12-digit account id.</summary>
    public async Task<string> IdAsync(CancellationToken ct) => (await WhoAsync(ct)).Account;

    /// <summary>Who AWS says we are, from a real call. Cached for the life of the target.</summary>
    public async Task<CallerIdentity> WhoAsync(CancellationToken ct)
    {
        if (_identity is not null) return _identity;
        await _gate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: three units reconciling in sequence is the normal case, but a
            // consumer running two procedures concurrently against one target is not forbidden.
            if (_identity is not null) return _identity;

            var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
            return _identity = new CallerIdentity(caller.Account, caller.Arn);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Who AWS says we are.</summary>
/// <param name="Account">The 12-digit account id — WHERE this deployment is going.</param>
/// <param name="Arn">The identity itself: a user, or a role that was assumed.</param>
internal sealed record CallerIdentity(string Account, string Arn);

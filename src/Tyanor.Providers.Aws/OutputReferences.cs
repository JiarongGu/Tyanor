using Amazon.CloudFormation;

namespace Tyanor.Providers.Aws;

/// <summary>
/// <c>"{unit}:{OutputKey}"</c> — a value one unit produces and a later one consumes, resolved at apply time.
///
/// <para><b>This is how an ordered list carries a dependency without becoming a graph.</b> The stack that
/// exports a bucket name is declared before the unit that fills it, and the reference is resolved when the
/// run reaches the second one — no edge, no resolution pass, nothing for a plan to have to render
/// (<c>units-not-graphs.md</c>).</para>
///
/// <para><b>Here because it is now used three times.</b> <c>bucketFrom</c> and <c>invalidateFrom</c> on a
/// content unit were the first two and shared a private copy; <c>parameterFrom.*</c> on a stack unit is the
/// third, and a third copy would have been the first one to word its refusal differently — the exact defect
/// <see cref="UnitContext.RequirePart"/> was extracted to fix one level down. See
/// <c>docs/DECISIONS.md</c> D34.</para>
/// </summary>
internal static class OutputReferences
{
    /// <summary>The form, quoted in every message about it so an operator reads one sentence rather than three.</summary>
    public const string Form = "\"{unit}:{OutputKey}\"";

    /// <summary>
    /// Parse a reference, or null when the setting is not set at all.
    /// </summary>
    /// <param name="setting">The setting's name, for the message. Not read — the caller supplies the value,
    /// because a group like <c>parameterFrom.*</c> has no single option to read.</param>
    /// <param name="unit">The unit the setting is on, so the message says WHERE.</param>
    /// <param name="value">The reference text, or null when unset.</param>
    /// <exception cref="AwsConfigurationException">It is set and does not parse.</exception>
    public static (string Unit, string Key)? Parse(string setting, string unit, string? value)
    {
        if (value is null) return null;

        var parts = value.Split(':', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new AwsConfigurationException(
                $"'{setting}' on unit '{unit}' is '{value}'; it must be {Form}.");

        return (parts[0], parts[1]);
    }

    /// <summary>Parse the reference an OPTION holds — the ordinary case.</summary>
    /// <param name="context">The unit.</param>
    /// <param name="option">The option naming the reference.</param>
    public static (string Unit, string Key)? Parse(UnitContext context, string option) =>
        Parse(option, context.Name, context.Option(option));

    /// <summary>
    /// Read a reference out of the named stack's outputs. Null when the setting is unset, when the stack is
    /// not deployed, or when it exports no such key.
    /// </summary>
    /// <param name="stacks">The stack driver, which is the only thing that can read a stack's outputs.</param>
    /// <param name="context">The unit holding the reference — its prefix names the stack.</param>
    /// <param name="setting">The setting's name, for a message.</param>
    /// <param name="value">The reference text, or null when unset.</param>
    /// <remarks>
    /// <b>Absent is null rather than an error</b>, deliberately: a plan of a deployment that does not exist
    /// yet resolves nothing, and that is a legitimate answer rather than a failure. A caller that cannot
    /// proceed without the value says so itself, in its own words, at the point it needed it.
    /// </remarks>
    public static async Task<string?> ResolveAsync(
        StackUnit stacks, UnitContext context, string setting, string? value)
    {
        if (Parse(setting, context.Name, value) is not var (unit, key)) return null;

        try
        {
            var outputs = await stacks.OutputsAsync($"{context.Request.Prefix}-{unit}", context.Cancellation);
            return outputs.TryGetValue(key, out var found) ? found : null;
        }
        catch (AmazonCloudFormationException e)
            when (CloudFormationPhases.IsStackMissing(e.ErrorCode, e.Message))
        {
            return null;
        }
    }

    /// <summary>Resolve the reference an OPTION holds — the ordinary case.</summary>
    /// <param name="stacks">The stack driver.</param>
    /// <param name="context">The unit.</param>
    /// <param name="option">The option naming the reference.</param>
    public static Task<string?> ResolveAsync(StackUnit stacks, UnitContext context, string option) =>
        ResolveAsync(stacks, context, option, context.Option(option));
}

namespace Tyanor;

/// <summary>One thing wrong with a procedure, found without touching the target.</summary>
/// <param name="Unit">Which unit it belongs to.</param>
/// <param name="Problem">Plain language, naming what was expected and what was found.</param>
public sealed record ValidationProblem(string Unit, string Problem)
{
    /// <summary>One line an operator can read.</summary>
    public override string ToString() => $"{Unit}: {Problem}";
}

/// <summary>
/// Everything wrong with a procedure and request, gathered in one pass with **no provider access at all**.
///
/// <para><b>Why offline is the whole point.</b> A misconfigured deployment should be refusable before an
/// account exists, before credentials are entered, and before anything is created — and the errors it would
/// otherwise produce arrive one at a time, three units in, after a run has already made things. Checking the
/// definition is a different question from checking the world, and only one of them needs a network.</para>
///
/// <para><b>It reports ALL of them.</b> Someone fixing a procedure wants the list, not the first item ten
/// times — the same reason <c>ContractSuite.AssertAllAsync</c> does.</para>
/// </summary>
/// <param name="Problems">Empty when there is nothing wrong.</param>
public sealed record Validation(IReadOnlyList<ValidationProblem> Problems)
{
    /// <summary>Nothing is wrong with the definition. Says nothing about the target.</summary>
    public bool Ok => Problems.Count == 0;

    /// <summary>Every problem, one per line — the message to put in front of a person.</summary>
    public override string ToString() =>
        Ok ? "The procedure is valid." : string.Join(Environment.NewLine, Problems);
}

/// <summary>
/// Everything wrong with ONE unit's configuration, gathered by running the resolvers an apply would run and
/// recording what each refuses instead of throwing at the first.
///
/// <para><b>Written four times before it was written once.</b> Every unit kind in both shipped providers
/// reached the same shape independently — a list, a loop over <c>(Action[])[…]</c>, a
/// <c>catch (DefinitionException)</c> adding the message, and a <c>Task.FromResult</c> to match the
/// signature. That is twice over the bar <c>UnitKindDriver</c> and <c>Registry&lt;T&gt;</c> were extracted
/// on, and the standing question in <c>CLAUDE.md</c> answers itself: a provider written outside this
/// repository would have to copy it, so it belongs here.</para>
///
/// <para><b>Why the checks are the APPLY's own resolvers.</b> <see cref="IUnitDriver.ValidateAsync"/> is
/// worth trusting only if it refuses exactly what a create would refuse. Writing the rule a second time in a
/// validation-shaped form gives you two rules, and they diverge the first time one is edited — so a check
/// here is a call to the same private resolver, and what it throws is the message the operator gets.</para>
///
/// <example>
/// <code>
/// public Task&lt;IReadOnlyList&lt;string&gt;&gt; ValidateAsync(UnitContext context) =>
///     new UnitProblems()
///         .Check(() => Command(context))
///         .Check(() => Port(context))
///         .Found();
/// </code>
/// </example>
/// </summary>
public sealed class UnitProblems
{
    private readonly List<string> _problems = [];

    /// <summary>
    /// Run one of the apply's resolvers and record its refusal, rather than letting it end the pass.
    /// </summary>
    /// <param name="resolve">
    /// The resolver, called for its effect. Anything it returns is ignored — this is here to find out whether
    /// it throws.
    /// </param>
    /// <remarks>
    /// <para>Only <see cref="DefinitionException"/> is caught, and that line is the whole discipline. It means
    /// "the procedure or the request is wrong", which is precisely what an offline check is looking for.
    /// Anything else — an <see cref="IOException"/>, a provider's own SDK exception — is a resolver reaching
    /// for the world, which <see cref="IUnitDriver.ValidateAsync"/> says it must not do, and swallowing it
    /// would turn that mistake into a silent pass.</para>
    /// <para>Each check is separate on purpose: an operator with both a missing command and an unparseable
    /// port should be told both, not told one and then the other on the next attempt.</para>
    /// </remarks>
    public UnitProblems Check(Action resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        try { resolve(); }
        catch (DefinitionException e) { _problems.Add(e.Message); }
        return this;
    }

    /// <summary>
    /// Record a problem that no resolver raises — one found by looking at the options themselves.
    /// </summary>
    /// <param name="problem">Plain language, naming what was expected and what to write instead.</param>
    /// <remarks>
    /// For the check that has nothing to call: "this unit names no destination at all" is true of the absence
    /// of two options together, so there is no single resolver whose refusal says it.
    /// </remarks>
    public UnitProblems Add(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        _problems.Add(problem);
        return this;
    }

    /// <summary>Everything found, in the shape <see cref="IUnitDriver.ValidateAsync"/> returns.</summary>
    /// <remarks>
    /// A snapshot, not the live list: a driver that kept the builder and added to it afterwards would
    /// otherwise change a result it had already handed back.
    /// </remarks>
    public Task<IReadOnlyList<string>> Found() =>
        Task.FromResult<IReadOnlyList<string>>([.. _problems]);
}

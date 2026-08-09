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

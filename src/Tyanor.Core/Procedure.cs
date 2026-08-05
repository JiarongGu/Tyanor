namespace Tyanor;

/// <summary>
/// One deployable thing inside a procedure — a stack, a chart, a service, a bucket of static files.
///
/// <para>Units are an ORDERED LIST, not a dependency graph. That is a deliberate ceiling: ordering covers
/// the overwhelming majority of real deployments (data before compute before edge) at a fraction of the
/// cost, and a graph is where tools of this kind become large. See
/// <c>.claude/rules/units-not-graphs.md</c> before adding edges.</para>
/// </summary>
/// <param name="Name">Stable identity within the procedure — also the resume key, so it must not change
/// between runs of the same procedure.</param>
/// <param name="Label">What to call it in front of a human ("Database", "Website").</param>
/// <param name="Weight">Relative share of the run's progress. Equal weights are fine; give a
/// ten-minute unit more than a ten-second one so a progress bar does not lie.</param>
public sealed record ProcedureUnit(string Name, string Label, int Weight = 1);

/// <summary>
/// An ordered set of units plus the direction they are applied in. A teardown is the same procedure read
/// backwards — edge before compute before data — so importers are gone before the thing they import from.
/// </summary>
/// <param name="Name">Procedure identity (also the history key).</param>
/// <param name="Units">Applied in order; torn down in reverse.</param>
public sealed record Procedure(string Name, IReadOnlyList<ProcedureUnit> Units)
{
    /// <summary>The order to APPLY in.</summary>
    public IEnumerable<ProcedureUnit> Forward() => Units;

    /// <summary>The order to REMOVE in — the exact reverse, never a separately maintained list.</summary>
    public IEnumerable<ProcedureUnit> Reverse() => Units.Reverse();

    /// <summary>Total weight, for progress arithmetic.</summary>
    public int TotalWeight => Units.Sum(u => u.Weight);
}

/// <summary>How loud a progress line is. Drives tone in a UI, nothing else.</summary>
public enum ProgressStatus
{
    /// <summary>Normal narration.</summary>
    Info,

    /// <summary>A step finished well.</summary>
    Success,

    /// <summary>Something went wrong. Not necessarily terminal — see <see cref="OperationOutcome"/>.</summary>
    Error,
}

/// <summary>
/// One live progress line. Written for the person watching, not for a log parser: a procedure that a
/// non-technical owner can run is one whose progress they can read.
/// </summary>
/// <param name="Unit">Which unit this concerns, or the procedure name for run-level lines.</param>
/// <param name="Message">Plain language. Not an exception message, not a status code.</param>
/// <param name="Percent">0–100, or -1 when the step genuinely has no measurable fraction —
/// which is honest, where a fabricated number is not.</param>
/// <param name="Status">Tone.</param>
public sealed record ProgressReport(string Unit, string Message, int Percent, ProgressStatus Status = ProgressStatus.Info);

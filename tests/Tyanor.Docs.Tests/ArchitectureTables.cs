using System.Text.RegularExpressions;
using Xunit;

namespace Tyanor.Docs.Tests;

/// <summary>
/// The reconcile tables in <c>docs/architecture/overview.md</c> are the CODE, written out for a reader —
/// so they are checked against it rather than trusted.
///
/// <para><b>Why this exists.</b> Every C# sample in the three consumer-facing documents is compiled, so a
/// renamed method breaks the build instead of rotting quietly. `overview.md` carries no samples at all —
/// it explains the model in prose and two tables — so nothing held it to anything. The tables are the one
/// part of it a machine can check, and they are also the part most worth checking: they ARE the resume
/// model, and a newcomer reads them to learn what the engine does.</para>
///
/// <para><b>The failure being prevented is silence, not error.</b> Adding a sixth <see cref="UnitPhase"/>
/// would leave the table complete-looking and one row short, and adding it is exactly the change during
/// which nobody rereads an architecture document. `Retain` arrived that way once already — D32 added a
/// third teardown answer, and the table had to be edited by hand for it.</para>
/// </summary>
public class ArchitectureTables
{
    private static readonly string Doc = File.ReadAllText(Repo.Path("docs", "architecture", "overview.md"));

    /// <summary>Rows of a markdown table, as their cells, with the header and separator dropped.</summary>
    private static List<string[]> Rows(string afterHeading, string firstColumn)
    {
        var body = Doc[Doc.IndexOf(afterHeading, StringComparison.Ordinal)..];

        return [.. body.Split('\n')
            .SkipWhile(l => !l.StartsWith($"| {firstColumn}", StringComparison.Ordinal))
            .Skip(2)                                            // the header row and its |---| separator
            .TakeWhile(l => l.StartsWith('|'))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())];
    }

    /// <summary>The first `Word` in backticks or bold — how the doc writes an enum member.</summary>
    private static string Symbol(string cell) =>
        Regex.Match(cell, @"[`*]{1,2}(\w+)[`*]{1,2}").Groups[1].Value;

    [Fact]
    public void The_apply_table_says_what_Reconcile_Decide_does()
    {
        var documented = Rows("## The reconcile table", "Phase")
            .ToDictionary(r => Symbol(r[0]), r => Symbol(r[1]), StringComparer.Ordinal);

        // Every phase, not just the ones somebody remembered to write down.
        foreach (var phase in Enum.GetValues<UnitPhase>())
        {
            Assert.True(documented.ContainsKey(phase.ToString()),
                $"overview.md's reconcile table has no row for UnitPhase.{phase}");
            Assert.Equal(Reconcile.Decide(phase).ToString(), documented[phase.ToString()]);
        }

        // …and nothing invented: a row for a phase that no longer exists is the other direction of the
        // same rot, and reads as authoritative.
        foreach (var row in documented.Keys)
            Assert.True(Enum.TryParse<UnitPhase>(row, out _),
                $"overview.md's reconcile table has a row for '{row}', which is not a UnitPhase");
    }

    [Fact]
    public void The_teardown_table_says_what_Reconcile_DecideDestroy_does()
    {
        // Three rows rather than one per phase — `Missing`, then removable and irreversible for everything
        // else — so it is read as the rule it states rather than by phase name.
        var rows = Rows("A teardown has its own table", "Phase");

        Assert.Equal(3, rows.Count);
        Assert.Equal(Reconcile.DecideDestroy(UnitPhase.Missing).ToString(), Symbol(rows[0][2]));
        Assert.Equal(Reconcile.DecideDestroy(UnitPhase.Ready, removable: true).ToString(), Symbol(rows[1][2]));
        Assert.Equal(Reconcile.DecideDestroy(UnitPhase.Ready, removable: false).ToString(), Symbol(rows[2][2]));
    }

    [Fact]
    public void The_document_is_right_that_Attach_issues_nothing()
    {
        // Stated in prose beside the table, and it is the claim the whole model rests on.
        Assert.Contains("`Reconcile.Mutates(Attach)` is `false`", Doc, StringComparison.Ordinal);
        Assert.False(Reconcile.Mutates(ReconcileAction.Attach));
    }
}

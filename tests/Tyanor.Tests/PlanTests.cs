using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The planning pass. A plan's job is to let someone decide whether to proceed, so what these cover is
/// mostly whether it tells the truth: that it changes nothing, that it flags a REPLACE, and that it says
/// so when another run is already in flight.
/// </summary>
public class PlanTests
{
    private static PlannedStep Step(string name, UnitPhase phase) =>
        new(new ProcedureUnit(name, name.ToUpperInvariant()), phase, Reconcile.Decide(phase));

    private static Plan PlanOf(params (string Name, UnitPhase Phase)[] units) =>
        new("site", "acme", units.Select(u => Step(u.Name, u.Phase)).ToList());

    [Fact]
    public void A_plan_over_settled_units_reports_the_updates_it_would_attempt()
    {
        // Update is listed as a CHANGE even though it may turn out to be a no-op: only the provider knows
        // whether the configuration actually differs, and claiming otherwise would be a guess presented
        // as a fact.
        var plan = PlanOf(("db", UnitPhase.Ready), ("api", UnitPhase.Ready));

        Assert.Equal(2, plan.Changes.Count);
        Assert.False(plan.IsNoOp);
        Assert.Empty(plan.Replacements);
    }

    [Fact]
    public void A_broken_unit_is_surfaced_as_a_replacement()
    {
        // The step worth a confirmation prompt — replacing usually means losing what the unit held.
        var plan = PlanOf(("db", UnitPhase.Broken), ("api", UnitPhase.Ready));

        var replaced = Assert.Single(plan.Replacements);
        Assert.Equal("db", replaced.Unit.Name);
        Assert.Equal(ReconcileAction.Recreate, replaced.Action);
    }

    [Fact]
    public void An_unwinding_unit_is_a_replacement_too()
        => Assert.Single(PlanOf(("db", UnitPhase.Unwinding)).Replacements);

    [Fact]
    public void Work_already_in_flight_is_flagged_and_is_not_counted_as_a_change()
    {
        // Applying now is safe — the engine attaches rather than conflicting — but the operator is about
        // to watch someone else's deployment, and should know that before wondering why their change
        // did not take effect.
        var plan = PlanOf(("db", UnitPhase.Ready), ("api", UnitPhase.Converging));

        Assert.True(plan.HasWorkInFlight);
        Assert.DoesNotContain(plan.Changes, s => s.Unit.Name == "api");
        Assert.False(plan.IsNoOp);                       // in-flight work is not "nothing to do"
    }

    [Fact]
    public void A_plan_with_nothing_to_do_says_so()
    {
        // Every unit missing would be a full create; every unit attached would be someone else's run.
        // "No-op" is neither — it is reserved for genuinely nothing to issue and nothing to wait on.
        var plan = new Plan("site", "acme", []);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Changes);
        Assert.False(plan.HasWorkInFlight);
    }

    [Fact]
    public void A_missing_unit_plans_a_create()
    {
        var step = Assert.Single(PlanOf(("db", UnitPhase.Missing)).Changes);

        Assert.Equal(ReconcileAction.Create, step.Action);
        Assert.True(step.Mutates);
    }

    [Fact]
    public void An_attach_step_is_explicitly_non_mutating()
        => Assert.False(Step("api", UnitPhase.Converging).Mutates);

    [Fact]
    public void Every_step_reads_as_a_sentence_naming_its_unit()
    {
        // A plan nobody can read is a plan nobody checks.
        foreach (var phase in Enum.GetValues<UnitPhase>())
        {
            var text = Step("db", phase).ToString();
            Assert.Contains("DB", text);
            Assert.DoesNotContain(phase.ToString(), text);   // operator language, not enum names
        }
    }
}

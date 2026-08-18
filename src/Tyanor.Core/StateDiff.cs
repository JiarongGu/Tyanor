namespace Tyanor;

/// <summary>
/// Compares what state records against what a refresh found — the pure half of "is my state in sync with
/// the real deployment?".
///
/// <para>Pure and static so it can be tested against fixed inputs, because a wrong answer here is
/// expensive in a specific way: it either hides a resource that has drifted, or invents one that has not,
/// and both erode the operator's trust in the number they are about to act on.</para>
/// </summary>
public static class StateDiff
{
    /// <summary>
    /// Diff one unit. <paramref name="recorded"/> is what Tyanor last wrote; <paramref name="actual"/> is
    /// what the provider reports now.
    /// </summary>
    public static IReadOnlyList<Drift> ForUnit(string unit, IReadOnlyList<ResourceState> recorded, IReadOnlyList<ResourceState> actual)
    {
        var drift = new List<Drift>();

        // Last wins on a duplicate id rather than throwing. A driver reporting one resource twice is a bug
        // in that driver — UnitDriverContract has a check for it — but a plan is the thing an operator runs
        // to FIND OUT what is wrong, so it must not be the thing that cannot run.
        var byId = new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        foreach (var r in actual) byId[r.Id] = r;

        foreach (var was in recorded)
        {
            if (!byId.TryGetValue(was.Id, out var now))
            {
                // Recorded but absent: deleted outside Tyanor, or never really created.
                drift.Add(new Drift(unit, was, ResourceChange.Destroy));
                continue;
            }
            // Asked rather than repeated: "unknown is not equal" is the load-bearing rule here, and writing
            // it in both places is how the two come to disagree.
            if (!Unchanged(was.Fingerprint, now.Fingerprint))
                drift.Add(new Drift(unit, now, ResourceChange.Change));
        }

        var known = recorded.Select(r => r.Id).ToHashSet();
        foreach (var now in actual.Where(r => !known.Contains(r.Id)))
            // Present but unrecorded: created outside Tyanor, or state was lost. Adopted on the next apply.
            drift.Add(new Drift(unit, now, ResourceChange.Add));

        return drift;
    }

    /// <summary>
    /// Whether two fingerprints represent the same thing — the rule <see cref="ForUnit"/> compares on,
    /// named so it can be tested directly.
    /// </summary>
    /// <remarks>
    /// A null fingerprint on EITHER side means the provider cannot tell whether the resource changed, and
    /// that is deliberately NOT "unchanged". An unnoticed change is worse than a conservative one, and the
    /// operator can see the fingerprint is unknown.
    /// </remarks>
    public static bool Unchanged(string? recorded, string? actual) =>
        recorded is not null && actual is not null && recorded == actual;
}

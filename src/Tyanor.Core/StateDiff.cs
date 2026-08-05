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
        var byId = actual.ToDictionary(r => r.Id);

        foreach (var was in recorded)
        {
            if (!byId.TryGetValue(was.Id, out var now))
            {
                // Recorded but absent: deleted outside Tyanor, or never really created.
                drift.Add(new Drift(unit, was, ResourceChange.Destroy));
                continue;
            }
            // A null fingerprint on EITHER side means the provider cannot tell whether it changed. Report
            // it as a change rather than assuming equality — an unnoticed change is worse than a
            // conservative one, and the operator can see the fingerprint is unknown.
            if (was.Fingerprint is null || now.Fingerprint is null || was.Fingerprint != now.Fingerprint)
                drift.Add(new Drift(unit, now, ResourceChange.Change));
        }

        var known = recorded.Select(r => r.Id).ToHashSet();
        foreach (var now in actual.Where(r => !known.Contains(r.Id)))
            // Present but unrecorded: created outside Tyanor, or state was lost. Adopted on the next apply.
            drift.Add(new Drift(unit, now, ResourceChange.Add));

        return drift;
    }

    /// <summary>
    /// Whether two fingerprints represent the same thing. Separate from <see cref="ForUnit"/> because
    /// "unknown is not equal" is the load-bearing rule and deserves to be nameable in a test.
    /// </summary>
    public static bool Unchanged(string? recorded, string? actual) =>
        recorded is not null && actual is not null && recorded == actual;
}

namespace Tyanor;

/// <summary>
/// Units an application brings of its OWN, registered alongside a provider's built-in kinds.
///
/// <para><b>What this is for.</b> A real deployment has steps that are nobody's vendor's business: verify a
/// database migration actually applied, warm a cache, prerender pages, call a health endpoint that means
/// something only to you. Before this, adding one meant writing a whole provider — and then it could not be
/// mixed with that vendor's units anyway, because a run is bound to one target. So those steps lived outside
/// the procedure, as code that ran after it, and got none of what the engine gives: no phase, no plan, no
/// resume, no classified failure.</para>
///
/// <para>Now they are units. A step with a readable phase gets reconciled like anything else — skipped when
/// it is already done, attached to when it is in flight, retried when it fails transiently — and it shows up
/// in a plan next to the stacks.</para>
///
/// <example>
/// <code>
/// var target = new AwsTarget(credentials, new CustomUnits
/// {
///     ["migration"] = new VerifyMigrationUnit(http),
/// });
///
/// var procedure = new Procedure("site",
/// [
///     new ProcedureUnit("db", "Database"),
///     new ProcedureUnit("api", "API"),
///     new ProcedureUnit("migration", "Database changes"),   // ["migration.kind"] = "migration"
///     new ProcedureUnit("web", "Website"),
/// ]);
/// </code>
/// </example>
///
/// <para><b>A target COPIES this when you construct it.</b> Registering a kind after the target exists changes
/// nothing — every provider snapshots, so that a run's set of kinds cannot change underneath it, and so that a
/// custom kind colliding with a built-in one is refused at the one moment somebody is there to read the
/// exception. Build it, hand it over, and hand the same instance to every target you use: the step you wrote
/// does not belong to the platform you first ran it on.</para>
///
/// <para><b>This is the develop-here-then-upstream path.</b> A step written in your application against
/// <see cref="IUnitDriver"/>, passing the contract suites, is the same thing a built-in kind is. If it turns
/// out to be general, it moves into a provider unchanged (<c>docs/DECISIONS.md</c> D15).</para>
/// </summary>
public sealed class CustomUnits : Dictionary<string, IUnitDriver>
{
    /// <summary>Nothing yet.</summary>
    public CustomUnits() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>
    /// How to read errors YOUR units throw, if they throw anything a provider's classifier would not
    /// recognise.
    /// </summary>
    /// <remarks>
    /// Without one, an error from a custom unit falls through the provider's classifier as unrecognised and
    /// the engine treats it as <see cref="FailureClass.Hard"/> — correct, but it means your step can never
    /// PAUSE. If yours has a transient failure worth retrying (an endpoint not warm yet) or a credential one
    /// worth resuming after re-authentication, this is how the engine learns to tell them apart.
    /// </remarks>
    public IFailureClassifier? Classifier { get; init; }
}

/// <summary>Reading an error by asking several classifiers in turn.</summary>
public static class FailureClassifiers
{
    /// <summary>
    /// The first classifier that recognises an error decides it; nulls in the list are skipped.
    /// </summary>
    /// <param name="classifiers">Asked in order. A provider's own belongs first — it knows its SDK.</param>
    /// <remarks>
    /// "Not mine" is a real answer, which is what makes chaining work at all: a classifier returning null is
    /// passing rather than voting, so the next one gets its turn and an error nobody claims still lands on
    /// <see cref="FailureClass.Hard"/> the way it should.
    /// </remarks>
    public static IFailureClassifier Chain(params IFailureClassifier?[] classifiers) =>
        new Chained([.. classifiers.Where(c => c is not null).Cast<IFailureClassifier>()]);

    private sealed class Chained(IReadOnlyList<IFailureClassifier> classifiers) : IFailureClassifier
    {
        public FailureClass? Classify(Exception error)
        {
            foreach (var classifier in classifiers)
                if (classifier.Classify(error) is { } known) return known;

            return null;
        }
    }
}

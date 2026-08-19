using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// <see cref="FailureClassifiers.Walk"/> — the exception-chain walk both shipped providers used to write by
/// hand, and which any classifier written outside this repository would have written a third time.
///
/// <para><c>error-classification.md</c> calls reading only the outermost exception "the most common way a
/// classifier goes quietly wrong", and the cost is specific: a credential failure read as hard ends a run
/// that should have paused, and throws away every unit already deployed. So the walk is worth testing on its
/// own rather than only through a provider that happens to use it.</para>
/// </summary>
public class FailureWalkTests
{
    /// <summary>A classifier that recognises exactly one type — enough to say WHERE the walk looked.</summary>
    private static Func<Exception, FailureClass?> Recognises<T>(FailureClass answer) where T : Exception =>
        e => e is T ? answer : null;

    [Fact]
    public void The_outermost_exception_is_asked_first()
        => Assert.Equal(FailureClass.Transient, FailureClassifiers.Walk(
            new TimeoutException(), Recognises<TimeoutException>(FailureClass.Transient)));

    [Fact]
    public void An_exception_nested_inside_a_generic_one_is_still_found()
    {
        // The ordinary wrapping case: providers put the informative exception inside something bland.
        var wrapped = new InvalidOperationException("could not start",
            new UnauthorizedAccessException("denied"));

        Assert.Equal(FailureClass.Credentials, FailureClassifiers.Walk(
            wrapped, Recognises<UnauthorizedAccessException>(FailureClass.Credentials)));
    }

    [Fact]
    public void An_error_nobody_recognises_is_null_rather_than_a_guess()
        // "Not mine" is a real answer — it is what lets several classifiers be chained, and the engine's
        // default for null is Hard, which is the safe one.
        => Assert.Null(FailureClassifiers.Walk(
            new InvalidOperationException("something new"), Recognises<TimeoutException>(FailureClass.Transient)));

    [Fact]
    public void A_credential_error_SECOND_in_an_AggregateException_is_found()
    {
        // THE case this walk was extracted to fix. AggregateException.InnerException returns only the FIRST
        // inner exception, so a plain `e = e.InnerException` chain never sees any sibling — and an aggregate
        // is exactly what a task-based provider call path adds on top. Before this, the run below ended as
        // Hard and discarded work that was intact, for want of looking one element to the right.
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new UnauthorizedAccessException("the token expired"));

        Assert.Equal(FailureClass.Credentials, FailureClassifiers.Walk(
            aggregate, Recognises<UnauthorizedAccessException>(FailureClass.Credentials)));
    }

    [Fact]
    public void Every_branch_of_an_aggregate_is_opened_not_just_the_first()
    {
        // The sibling is itself a wrapper, so finding it needs the walk to descend INTO a branch it reached
        // sideways — not merely to glance at each inner exception.
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new InvalidOperationException("wrapper", new TimeoutException()));

        Assert.Equal(FailureClass.Transient, FailureClassifiers.Walk(
            aggregate, Recognises<TimeoutException>(FailureClass.Transient)));
    }

    [Fact]
    public void The_first_recognised_exception_wins_and_the_walk_stops()
    {
        // Depth-first, in declared order: the specific cause is nearly always the nested one, so it should
        // beat a general sibling sitting later in the list.
        var asked = new List<string>();
        var aggregate = new AggregateException("both failed",
            new TimeoutException("first"),
            new TimeoutException("second"));

        // The aggregate's own Message composes its inners' messages into itself, so it is identified by type
        // rather than by text — the assertion is about ORDER, not about how .NET words a wrapper.
        var answer = FailureClassifiers.Walk(aggregate, e =>
        {
            if (e is not AggregateException) asked.Add(e.Message);
            return e is TimeoutException ? FailureClass.Transient : null;
        });

        Assert.Equal(FailureClass.Transient, answer);
        Assert.Equal(["first"], asked);      // "second" is never reached
    }

    [Fact]
    public void A_classifier_that_recognises_nothing_is_asked_about_every_exception_exactly_once()
    {
        // Pins that the walk terminates and does not revisit: an aggregate's own InnerException duplicates
        // its first entry, so handling both branches would ask about that one twice.
        var asked = new List<string>();
        var aggregate = new AggregateException("all failed",
            new InvalidOperationException("one"),
            new InvalidOperationException("two", new TimeoutException("three")));

        var aggregates = 0;
        Assert.Null(FailureClassifiers.Walk(aggregate, e =>
        {
            if (e is AggregateException) aggregates++; else asked.Add(e.Message);
            return null;
        }));

        // Once, not twice: an aggregate's own InnerException duplicates its first entry, so a walk that
        // followed both would ask about "one" a second time.
        Assert.Equal(1, aggregates);
        Assert.Equal(["one", "two", "three"], asked);
    }

    [Fact]
    public void It_refuses_a_null_error_or_a_null_classifier()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FailureClassifiers.Walk(null!, Recognises<TimeoutException>(FailureClass.Transient)));
        Assert.Throws<ArgumentNullException>(() => FailureClassifiers.Walk(new TimeoutException(), null!));
    }
}

/// <summary>
/// <see cref="FailureClassifiers.Chain"/> — how an application's own classifier is asked after the
/// provider's, which is what lets a <see cref="CustomUnits"/> step pause rather than only ever fail.
/// </summary>
public class ClassifierChainTests
{
    private sealed class Answers(FailureClass? answer) : IFailureClassifier
    {
        public int Asked { get; private set; }

        public FailureClass? Classify(Exception error)
        {
            Asked++;
            return answer;
        }
    }

    [Fact]
    public void The_first_classifier_that_recognises_an_error_decides_it()
    {
        var first = new Answers(FailureClass.Credentials);
        var second = new Answers(FailureClass.Transient);

        Assert.Equal(FailureClass.Credentials,
            FailureClassifiers.Chain(first, second).Classify(new TimeoutException()));
        Assert.Equal(0, second.Asked);       // …and the rest are not asked at all
    }

    [Fact]
    public void A_classifier_returning_null_passes_rather_than_votes()
    {
        // The property the whole mechanism rests on: "not mine" hands the question on, so a provider that
        // does not recognise an application's own exception does not thereby decide it.
        var provider = new Answers(null);
        var mine = new Answers(FailureClass.Transient);

        Assert.Equal(FailureClass.Transient,
            FailureClassifiers.Chain(provider, mine).Classify(new TimeoutException()));
        Assert.Equal(1, provider.Asked);
    }

    [Fact]
    public void An_error_nobody_claims_stays_null_so_the_engine_treats_it_as_hard()
        => Assert.Null(FailureClassifiers.Chain(new Answers(null), new Answers(null))
            .Classify(new TimeoutException()));

    [Fact]
    public void A_null_in_the_list_is_skipped_because_a_target_may_have_no_custom_classifier()
        // Both shipped targets pass `custom?.Classifier` straight in, which is null whenever the application
        // brought units but no classifier for them.
        => Assert.Equal(FailureClass.Transient,
            FailureClassifiers.Chain(null, new Answers(FailureClass.Transient), null)
                .Classify(new TimeoutException()));
}

using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The library's public surface, held to a checked-in baseline.
///
/// <para>Read <c>tests/Shared/ApiSurface.cs</c> for why this exists. The short version: from 0.1.0 the public
/// surface is a promise, and nothing else in this repository can see it change — the build succeeds, every
/// test passes, and a <c>public</c> that should have been <c>internal</c> ships forever. This makes the
/// surface a file, so changing it is a diff somebody reads.</para>
///
/// <para>It matters most for THIS assembly, which merged four packages into one (D26). Types that were public
/// only because a sibling assembly needed to see them no longer have that excuse, and the baseline is where
/// the argument for each one gets made out loud.</para>
/// </summary>
public class ApiSurfaceTests
{
    [Fact]
    public void The_public_surface_is_what_the_baseline_records() =>
        ApiSurface.MatchesBaseline(typeof(Procedure).Assembly);
}

/// <summary>
/// The renderer itself, which had no tests and therefore had a hole.
///
/// <para><b>A baseline cannot check its own renderer, and that is the whole reason these exist.</b> A
/// rendering rule that silently drops a kind of member produces a smaller file — and a smaller file is
/// exactly what the baseline then records, agrees with for ever, and reports as green. So the gate meant to
/// make an accidental <c>public</c> visible can have a blind spot that nothing downstream of it can see.</para>
///
/// <para>It did: every user-defined operator was dropped, because the exclusion meant to keep them tested
/// for a member named exactly <c>"op_"</c>, which no member is. An implicit conversion — the sort of thing
/// added without anyone thinking of it as API, and impossible to take back once shipped — would not have
/// appeared in any diff.</para>
/// </summary>
public class ApiSurfaceRendererTests
{
    /// <summary>A type built to exercise the rendering rules, and nothing else.</summary>
    public sealed class Probe
    {
        /// <summary>A user-declared operator: real surface, and special-name like an accessor.</summary>
        public static Probe operator +(Probe left, Probe right) => left;

        /// <summary>A user-declared conversion. The one nobody thinks of as API until it cannot be removed.</summary>
        public static implicit operator string(Probe probe) => nameof(Probe);

        /// <summary>An ordinary property, which must be reported once, through the property.</summary>
        public string Name { get; init; } = "";

        /// <summary>An ordinary method.</summary>
        public void Do() { }
    }

    /// <summary>A record, whose equality operators the compiler writes.</summary>
    public sealed record Boilerplate(string Value);

    private static string Rendered<T>() => ApiSurface.Render(typeof(T));

    [Fact]
    public void A_user_declared_operator_is_recorded()
        // It ships, it cannot be withdrawn, and it was invisible.
        => Assert.Contains("op_Addition", Rendered<Probe>());

    [Fact]
    public void A_user_declared_CONVERSION_is_recorded()
        // The one that gets added by accident: an implicit conversion changes what compiles for a consumer.
        => Assert.Contains("op_Implicit", Rendered<Probe>());

    [Fact]
    public void The_operators_a_RECORD_generates_are_not()
    {
        // Their presence is implied by `record`, and listing them would turn a compiler upgrade into an API
        // change. Filtered by NAME, which is what the special-name rule now leaves to be done.
        var rendered = Rendered<Boilerplate>();

        Assert.DoesNotContain("op_Equality", rendered);
        Assert.DoesNotContain("op_Inequality", rendered);
    }

    [Fact]
    public void A_property_is_reported_once_through_the_property()
    {
        // Accessors ARE special-name, so the rule that keeps operators must not also let get_/set_ back in.
        var rendered = Rendered<Probe>();

        Assert.Contains("string Name { get; init; }", rendered);
        Assert.DoesNotContain("get_Name", rendered);
        Assert.DoesNotContain("set_Name", rendered);
    }

    [Fact]
    public void An_ordinary_member_still_appears()
        // The check that stops all of the above being satisfied by rendering nothing at all.
        => Assert.Contains("void Do()", Rendered<Probe>());
}

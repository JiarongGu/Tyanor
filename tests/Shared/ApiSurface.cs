using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Tyanor.Tests.Support;

/// <summary>
/// The public surface of a shipped assembly, rendered as sorted text and compared against a checked-in
/// baseline under <c>tests/ApiBaselines/</c>.
///
/// <para><b>Why this exists, and why now.</b> Before 0.1.0 the public surface was free. From 0.1.0 it is a
/// promise: a removed member or a changed signature breaks someone's build, and the cheapest place to notice
/// that is a diff in a pull request rather than an issue after the fact. Nothing else in this repository can
/// see an API change — the build is happy, every test passes, and a `public` that should have been `internal`
/// ships forever. This turns "did the surface change?" into a line in a diff.</para>
///
/// <para><b>It is not a policy check.</b> It never says an addition is wrong; the baseline is a record, not a
/// rule. A deliberate change is one line of `TYANOR_UPDATE_API=1 dotnet test` and then a reviewer reading the
/// diff — which is the whole point, because the reviewer is the part that was missing.</para>
///
/// <para><b>What it renders, and what it leaves out.</b> Declared members only, so each declaration appears
/// exactly once and an inherited member is listed on the type that declares it. Record and value boilerplate
/// (<c>Equals</c>, <c>GetHashCode</c>, <c>Deconstruct</c>, <c>op_Equality</c>, …) is skipped: the compiler
/// writes it, its presence is implied by <c>record</c>, and including it would mean a compiler upgrade
/// showing up as an API change. Nullable reference annotations are absent too — reflection does not carry
/// them usefully, so a change from <c>string</c> to <c>string?</c> is invisible here. It is a net, not a
/// proof.</para>
///
/// <para>Shared through <c>tests/Directory.Build.props</c> so each assembly's surface is guarded beside its
/// own tests — a provider's API change fails the provider's test project, and a provider written outside this
/// repository can copy this file and get the same thing.</para>
/// </summary>
internal static class ApiSurface
{
    /// <summary>Set <c>TYANOR_UPDATE_API=1</c> to rewrite the baseline instead of failing.</summary>
    private const string UpdateVariable = "TYANOR_UPDATE_API";

    /// <summary>
    /// Fails unless <paramref name="assembly"/>'s public surface matches its baseline.
    /// </summary>
    public static void MatchesBaseline(Assembly assembly)
    {
        var name = assembly.GetName().Name!;
        var baseline = Path.Combine(RepoRoot(), "tests", "ApiBaselines", $"{name}.txt");
        var rendered = Render(assembly);

        if (Environment.GetEnvironmentVariable(UpdateVariable) is not (null or ""))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllText(baseline, rendered);
            return;
        }

        Assert.True(File.Exists(baseline),
            $"{name} has no API baseline. Create it with `{UpdateVariable}=1 dotnet test` and commit " +
            $"{Rel(baseline)} — reviewing that file IS the review of this assembly's public surface.");

        var recorded = File.ReadAllText(baseline);
        if (Normalize(recorded) == Normalize(rendered)) return;

        // The actual is written out so the fix is a copy rather than a transcription from a test log.
        var actual = baseline + ".actual";
        File.WriteAllText(actual, rendered);

        Assert.Fail(
            $"{name}'s public surface no longer matches {Rel(baseline)}.\n\n{Difference(recorded, rendered)}\n" +
            $"If the change is deliberate, run `{UpdateVariable}=1 dotnet test` and commit the baseline — a\n" +
            $"reviewer reading that diff is what this test exists to cause. The rendered surface is also at\n" +
            $"{Rel(actual)}. If it is NOT deliberate, something became public that should not have.");
    }

    /// <summary>The whole exported surface, sorted so the file is stable across runs and platforms.</summary>
    internal static string Render(Assembly assembly)
    {
        var text = new StringBuilder();
        text.Append("# Public API surface of ").Append(assembly.GetName().Name)
            .Append(". Generated — see tests/Shared/ApiSurface.cs.\n");

        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            text.Append('\n').Append(Render(type));

        return text.ToString();
    }

    /// <summary>
    /// One type's declaration and members, as they appear in a baseline.
    /// </summary>
    /// <remarks>
    /// Separate from the assembly walk so the renderer itself can be tested against a type built to probe
    /// it. A gate is only worth what its own tests are worth, and this one had a hole that no baseline
    /// could have shown: a rendering rule that drops something renders a smaller file, and a smaller file
    /// is exactly what a baseline records without complaint.
    /// </remarks>
    internal static string Render(Type type)
    {
        var text = new StringBuilder().Append(Declaration(type)).Append('\n');
        foreach (var member in Members(type)) text.Append("    ").Append(member).Append('\n');

        return text.ToString();
    }

    // ── rendering ────────────────────────────────────────────────────────────────────────────────

    private static string Declaration(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : IsRecord(type) ? (type.IsSealed ? "sealed record" : "record")
            : type.IsValueType ? "struct"
            : type is { IsAbstract: true, IsSealed: true } ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";

        // Base types and interfaces are surface: dropping one breaks a consumer who assigned to it.
        var bases = new List<string>();
        if (type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum))
            bases.Add(Name(b));
        bases.AddRange(Interfaces(type).Select(Name).OrderBy(n => n, StringComparer.Ordinal));

        var declared = $"{kind} {type.FullName}{Parameters(type)}";
        return bases.Count == 0 ? declared : $"{declared} : {string.Join(", ", bases)}";
    }

    /// <summary>Interfaces this type itself introduces — the ones a base already had belong to the base.</summary>
    private static IEnumerable<Type> Interfaces(Type type)
    {
        var inherited = (type.BaseType?.GetInterfaces() ?? []).ToHashSet();
        return type.GetInterfaces().Where(i => i.IsPublic && !inherited.Contains(i));
    }

    private static IEnumerable<string> Members(Type type)
    {
        if (type.IsEnum)
            // Values, not just names: renumbering an enum silently changes what a persisted number means.
            return Enum.GetValuesAsUnderlyingType(type).Cast<object>()
                .Select((v, i) => $"{Enum.GetNames(type)[i]} = {v}")
                .Order(StringComparer.Ordinal);

        const BindingFlags Visible =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return type.GetMembers(Visible)
            .Where(Interesting)
            .Select(Render)
            .Order(StringComparer.Ordinal);
    }

    private static bool Interesting(MemberInfo member)
    {
        // Backing fields and closures carry '<' in the name.
        if (member.Name.Contains('<')) return false;

        // Accessors are special-name and are reported through their property or event instead, where
        // `get`/`set`/`init` is more legible.
        //
        // An OPERATOR is special-name too, and is real public surface: a user-defined conversion that ships
        // can never be taken back, and it is precisely the sort of thing added without anyone thinking of it
        // as API. So `op_` is kept here and the record-generated pair is dropped by NAME below.
        //
        // This used to read `and not { Name: "op_" }`, which matches a member called exactly "op_" — no
        // member ever is — so the exclusion was inert, every operator was dropped, and the `op_Equality`
        // entry in the boilerplate list below was unreachable. A baseline could not have caught it: a
        // rendering rule that drops something renders a smaller file, and a smaller file is what the
        // baseline then records.
        if (member is MethodInfo { IsSpecialName: true }
            && !member.Name.StartsWith("op_", StringComparison.Ordinal)) return false;

        if (member is Type) return true;      // a nested public type; listed in full on its own line too

        // Record and value boilerplate. Its presence is implied by `record`, and listing it would turn a
        // compiler upgrade into an API change.
        return member.Name is not ("Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "PrintMembers"
            or "EqualityContract" or "op_Equality" or "op_Inequality" or "Finalize" or "MemberwiseClone");
    }

    private static string Render(MemberInfo member) => member switch
    {
        ConstructorInfo c => $".ctor({Arguments(c.GetParameters())})",

        PropertyInfo p =>
            $"{Modifiers(p.GetMethod ?? p.SetMethod!)}{Name(p.PropertyType)} {p.Name} " +
            $"{{ {(p.GetMethod is not null ? "get; " : "")}{Setter(p)}}}",

        MethodInfo m =>
            $"{Modifiers(m)}{Name(m.ReturnType)} {m.Name}{Parameters(m)}({Arguments(m.GetParameters())})",

        FieldInfo f => $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{Name(f.FieldType)} {f.Name}",

        EventInfo e => $"event {Name(e.EventHandlerType!)} {e.Name}",

        Type t => $"nested {t.Name}",

        _ => member.Name,
    };

    /// <summary><c>init</c> and <c>set</c> are different promises, so the baseline distinguishes them.</summary>
    private static string Setter(PropertyInfo property) =>
        property.SetMethod is not { } setter ? ""
        : setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init; "
        : "set; ";

    private static string Modifiers(MethodBase method) =>
        method.IsStatic ? "static "
        : method is MethodInfo { IsAbstract: true } ? "abstract "
        : method is MethodInfo { IsVirtual: true, IsFinal: false } ? "virtual "
        : "";

    private static string Arguments(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{Name(p.ParameterType)} {p.Name}" +
            (p.HasDefaultValue ? " = " + (p.DefaultValue?.ToString() ?? "null") : "")));

    private static string Parameters(MethodInfo method) => Arguments(method.GetGenericArguments());

    private static string Parameters(Type type) => Arguments(type.GetGenericArguments());

    private static string Arguments(Type[] generics) =>
        generics.Length == 0 ? "" : $"<{string.Join(", ", generics.Select(Name))}>";

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(int)] = "int", [typeof(long)] = "long",
        [typeof(string)] = "string", [typeof(object)] = "object", [typeof(double)] = "double",
        [typeof(byte)] = "byte", [typeof(char)] = "char", [typeof(decimal)] = "decimal",
    };

    private static string Name(Type type)
    {
        if (type.IsByRef || type.IsPointer) return Name(type.GetElementType()!);
        if (type.IsArray) return Name(type.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(type) is { } inner) return Name(inner) + "?";
        if (Aliases.TryGetValue(type, out var alias)) return alias;
        if (!type.IsGenericType) return type.Name;

        return $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    // ── the mechanics of comparing and reporting ─────────────────────────────────────────────────

    /// <summary>Line endings only. A CRLF checkout must not read as every line having changed.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd() + "\n";

    /// <summary>The lines that differ, both ways, capped — a full dump buries the one line that matters.</summary>
    private static string Difference(string recorded, string rendered)
    {
        var was = Normalize(recorded).Split('\n');
        var now = Normalize(rendered).Split('\n');

        var gone = was.Except(now).ToArray();
        var added = now.Except(was).ToArray();
        var report = new StringBuilder();

        // Removals first, deliberately: an addition is a new promise, a removal breaks an existing one.
        foreach (var line in gone.Take(15)) report.Append("  - ").Append(line.Trim()).Append('\n');
        if (gone.Length > 15) report.Append($"  … and {gone.Length - 15} more removed\n");
        foreach (var line in added.Take(15)) report.Append("  + ").Append(line.Trim()).Append('\n');
        if (added.Length > 15) report.Append($"  … and {added.Length - 15} more added\n");

        return report.ToString();
    }

    /// <summary>
    /// The repository root, stamped in by <c>tests/Directory.Build.props</c> at build time.
    ///
    /// <para>Not discovered by walking up from the output directory, which is the usual trick and breaks the
    /// moment anything runs the assembly from elsewhere.</para>
    /// </summary>
    private static string RepoRoot() =>
        typeof(ApiSurface).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value
        ?? throw new InvalidOperationException(
            "No RepoRoot in assembly metadata — tests/Directory.Build.props is meant to stamp it.");

    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
}

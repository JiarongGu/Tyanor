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
/// between runs of the same procedure. Becomes a directory name and a provider resource name, so it is
/// checked the same way <see cref="DeploymentRequest.Prefix"/> is: letters, digits, <c>-</c>, <c>_</c> and
/// <c>.</c> only, no leading dot, no <c>..</c>, at most 255 characters.</param>
/// <param name="Label">What to call it in front of a human ("Database", "Website"). Free text — this one is
/// only ever shown.</param>
/// <param name="Weight">Relative share of the run's progress. Equal weights are fine; give a
/// ten-minute unit more than a ten-second one so a progress bar does not lie.</param>
public sealed record ProcedureUnit(string Name, string Label, int Weight = 1)
{
    private readonly string _name = Identifiers.Require(Name, "unit name");
    private readonly int _weight = Weight > 0
        ? Weight
        : throw new ArgumentOutOfRangeException(nameof(Weight), Weight,
            "A unit's weight is its share of the progress bar and must be at least 1. Zero makes the unit " +
            "invisible while it runs; negative makes progress go backwards.");

    /// <inheritdoc cref="Name"/>
    public string Name
    {
        get => _name;
        init => _name = Identifiers.Require(value, "unit name");
    }

    /// <inheritdoc cref="Weight"/>
    public int Weight
    {
        get => _weight;
        init => _weight = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A unit's weight must be at least 1.");
    }
}

/// <summary>
/// An ordered set of units plus the direction they are applied in. A teardown is the same procedure read
/// backwards — edge before compute before data — so importers are gone before the thing they import from.
/// </summary>
/// <param name="Name">Procedure identity (also the history and state key).</param>
/// <param name="Units">Applied in order; torn down in reverse.</param>
public sealed record Procedure(string Name, IReadOnlyList<ProcedureUnit> Units)
{
    private readonly string _name = Identifiers.Require(Name, "procedure name");
    private readonly IReadOnlyList<ProcedureUnit> _units = Checked(Units);

    /// <inheritdoc cref="Name"/>
    public string Name
    {
        get => _name;
        init => _name = Identifiers.Require(value, "procedure name");
    }

    /// <inheritdoc cref="Units"/>
    public IReadOnlyList<ProcedureUnit> Units
    {
        get => _units;
        init => _units = Checked(value);
    }

    /// <summary>
    /// Whether this procedure is a NARROWING of a larger one — the result of <see cref="Only"/>.
    /// </summary>
    /// <remarks>
    /// It exists so a plan can tell "the operator left this unit out on purpose" from "state holds a unit
    /// the code no longer has". Both look identical from inside a narrowed procedure, and reporting the
    /// first as the second would make every targeted run cry orphan over the units it was told to skip.
    /// See <see cref="Plan.Orphaned"/>.
    /// </remarks>
    public bool IsNarrowed { get; private init; }

    /// <summary>The order to APPLY in.</summary>
    public IEnumerable<ProcedureUnit> Forward() => Units;

    /// <summary>The order to REMOVE in — the exact reverse, never a separately maintained list.</summary>
    public IEnumerable<ProcedureUnit> Reverse() => Units.Reverse();

    /// <summary>Total weight, for progress arithmetic.</summary>
    public int TotalWeight => Units.Sum(u => u.Weight);

    /// <summary>
    /// The same procedure narrowed to some of its units, in their original order — Terraform's
    /// <c>-target</c>, and the answer to "just push the website again".
    /// </summary>
    /// <param name="units">Unit names to keep. Order here is ignored; the procedure's own order is kept.</param>
    /// <exception cref="ArgumentException">A name that is not in this procedure, or none given.</exception>
    /// <remarks>
    /// <para><b>Why this is a method and not left to the caller.</b> Constructing a smaller
    /// <see cref="Procedure"/> by hand already worked, and nobody would find it — while the deployer this was
    /// extracted from had a whole dedicated method for exactly one case of it, because redeploying a website
    /// takes seconds and reconciling three stacks to do it takes minutes.</para>
    /// <para><b>Safer than Terraform's version</b>, because there is no dependency graph to skip. A subset of
    /// an ordered list is still ordered, so the units that do run are in the same relative order they always
    /// were; the only thing narrowing can do is leave something out, which the plan then shows.</para>
    /// <para><b>It narrows a DESTROY too</b>, and that is worth knowing before using it that way: it destroys
    /// only what it names, in reverse of the original order. Preview it — a narrowed destroy plan lists
    /// exactly what will go.</para>
    /// <para>An unknown name is refused rather than ignored. A typo that quietly deploys nothing and reports
    /// success is the worst way for this to be wrong.</para>
    /// </remarks>
    public Procedure Only(params string[] units)
    {
        ArgumentNullException.ThrowIfNull(units);

        var wanted = new HashSet<string>(units, StringComparer.OrdinalIgnoreCase);
        var have = Units.Select(u => u.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = units.Where(u => !have.Contains(u)).ToList();

        if (unknown.Count > 0)
            throw new ArgumentException(
                $"'{Name}' has no unit called {string.Join(" or ", unknown.Select(u => $"'{u}'"))}. " +
                $"It has: {string.Join(", ", Units.Select(u => u.Name))}.", nameof(units));

        // The procedure's order, not the caller's — narrowing must not silently reorder a deployment.
        return this with
        {
            Units = Units.Where(u => wanted.Contains(u.Name)).ToList(),
            IsNarrowed = true,
        };
    }

    /// <summary>
    /// Refuse a procedure that cannot mean what it says.
    /// </summary>
    /// <remarks>
    /// <para><b>Duplicate names are the one worth catching.</b> A unit's name IS its address — the stack
    /// called <c>{prefix}-{name}</c>, the directory at <c>{root}/{prefix}/{name}</c>, its entry in state.
    /// Two units sharing one would deploy on top of each other and the second would silently overwrite the
    /// first's state, which looks like a unit that quietly stopped existing.</para>
    /// <para>Compared case-INSENSITIVELY, because on Windows <c>Api</c> and <c>api</c> are the same
    /// directory even though they are different strings — so a pair that looks fine on the machine it was
    /// written on collides on the machine it deploys to.</para>
    /// </remarks>
    private static IReadOnlyList<ProcedureUnit> Checked(IReadOnlyList<ProcedureUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count == 0)
            throw new ArgumentException("A procedure needs at least one unit.", nameof(units));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
            if (!seen.Add(unit.Name))
                throw new ArgumentException(
                    $"Two units are both called '{unit.Name}'. A unit's name is its address — the stack, the " +
                    "directory, its entry in state — so two of them would deploy on top of each other.",
                    nameof(units));

        return units;
    }
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
/// <param name="Percent">
/// 0–100, or -1 when the step genuinely has no measurable fraction — which is honest, where a fabricated
/// number is not.
/// <para><b>A DRIVER reports progress through its OWN unit; the engine rescales it into the run.</b> A
/// stack halfway through its resources reports 50, and an operator watching a four-unit procedure sees that
/// arrive as whatever share of the whole it actually is. Without the rule the number has no frame of
/// reference — the engine has always emitted run-relative percentages, so a driver emitting its own would
/// have been read as one, and a unit half done would have shown as a run half done.</para>
/// <para>-1 survives the rescaling as -1. Unknown does not become a fraction of anything.</para>
/// </param>
/// <param name="Status">Tone.</param>
public sealed record ProgressReport(string Unit, string Message, int Percent, ProgressStatus Status = ProgressStatus.Info);

namespace Tyanor.Testing;

/// <summary>What one contract check found.</summary>
/// <param name="Name">The check's name, stable enough to reference in a skip list or a bug report.</param>
/// <param name="Passed">Whether the implementation satisfied it.</param>
/// <param name="Detail">Why it failed, or null. Written for whoever has to fix it.</param>
public sealed record ContractCheck(string Name, bool Passed, string? Detail = null)
{
    /// <summary>One line, for a test runner that shows only a message.</summary>
    public override string ToString() => Passed ? $"{Name}: ok" : $"{Name}: FAILED — {Detail}";
}

/// <summary>
/// Raised by <see cref="ContractSuite.AssertAllAsync"/> when an implementation does not satisfy the contract.
/// </summary>
/// <param name="message">Every failing check, one per line.</param>
public sealed class ContractException(string message) : Exception(message);

/// <summary>
/// A set of behaviours an implementation MUST have for the engine to work as documented.
///
/// <para><b>This is the entry ticket.</b> Tyanor's seams are meant to be implemented outside this
/// repository — your own provider, your own state store — and "it compiles" is a much weaker claim than "it
/// behaves the way the engine assumes". These suites are that second claim, written down and runnable. An
/// implementation that passes one can be adopted into this repository, or trusted outside it, on evidence
/// rather than on reading.</para>
///
/// <para><b>No test framework is required.</b> A suite is a plain object that returns results, so it works
/// under xUnit, NUnit, MSTest, or a console app. Tyanor.Testing takes no package dependencies for the same
/// reason Core and Engine do not: a library that makes you adopt its test framework to check your own code
/// has overreached.</para>
///
/// <example>
/// The whole suite as one test:
/// <code>
/// [Fact]
/// public Task MyStore_satisfies_the_contract() =>
///     new StateStoreContract(() => new MyStateStore()).AssertAllAsync();
/// </code>
/// Or one test per check, so each reports under its own name — <see cref="Checks"/> is just a list of
/// strings, which every test framework can turn into cases:
/// <code>
/// private static readonly StateStoreContract Suite = new(() => new MyStateStore());
///
/// public static TheoryData&lt;string&gt; Checks()
/// {
///     var data = new TheoryData&lt;string&gt;();
///     foreach (var check in Suite.Checks) data.Add(check);
///     return data;
/// }
///
/// [Theory, MemberData(nameof(Checks))]
/// public Task Satisfies(string check) => Suite.AssertAsync(check);
/// </code>
/// </example>
/// </summary>
public abstract class ContractSuite
{
    /// <summary>What this suite is a contract for — <c>"IStateStore"</c>, <c>"IUnitDriver"</c>.</summary>
    public abstract string Subject { get; }

    /// <summary>Every check's name, in a stable order.</summary>
    public IReadOnlyList<string> Checks => Cases.Select(c => c.Name).ToList();

    /// <summary>
    /// The checks themselves. Implemented by each suite; a check returns null when satisfied and a reason
    /// when not, so the common case reads as an early return rather than as an assertion library.
    /// </summary>
    protected abstract IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases { get; }

    /// <summary>Run one check by name.</summary>
    /// <param name="name">The check's name, from <see cref="Checks"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentException">No check has that name — usually a rename that outran a skip list.</exception>
    public async Task<ContractCheck> RunAsync(string name, CancellationToken ct = default)
    {
        var check = Cases.FirstOrDefault(c => c.Name == name);
        if (check.Run is null)
            throw new ArgumentException(
                $"{Subject} has no check named '{name}'. It has: {string.Join(", ", Checks)}.", nameof(name));

        try
        {
            var problem = await check.Run(ct);
            return new ContractCheck(name, problem is null, problem);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // A check that throws is a failure like any other. An implementation that throws where the
            // contract says it must return is exactly what this exists to catch, and letting the exception
            // escape would report it as a broken TEST rather than a broken implementation.
            return new ContractCheck(name, false, $"threw {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Run every check.</summary>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<ContractCheck>> RunAllAsync(CancellationToken ct = default)
    {
        var results = new List<ContractCheck>();
        foreach (var name in Checks) results.Add(await RunAsync(name, ct));
        return results;
    }

    /// <summary>Run one check and throw if it failed — the shape a test method wants.</summary>
    /// <param name="name">The check's name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ContractException">The check failed.</exception>
    public async Task AssertAsync(string name, CancellationToken ct = default)
    {
        var result = await RunAsync(name, ct);
        if (!result.Passed) throw new ContractException($"{Subject} — {result}");
    }

    /// <summary>
    /// Run everything and throw with EVERY failure listed, not just the first.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ContractException">One or more checks failed.</exception>
    /// <remarks>
    /// All of them on purpose: someone bringing a new implementation wants the whole list of what is missing,
    /// not one item at a time across ten runs.
    /// </remarks>
    public async Task AssertAllAsync(CancellationToken ct = default)
    {
        var failures = (await RunAllAsync(ct)).Where(r => !r.Passed).ToList();
        if (failures.Count == 0) return;

        throw new ContractException(
            $"{Subject}: {failures.Count} of {Checks.Count} contract checks failed." +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => "  " + f)));
    }
}

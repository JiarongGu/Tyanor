using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tyanor.Engine.State;

namespace Tyanor.Engine;

/// <summary>
/// How Tyanor is set up. Everything here is a CHOICE the consuming application makes — a library that
/// decides where an operator's run history lives, or how long it retries, has overreached.
/// </summary>
public sealed class TyanorOptions
{
    /// <summary>
    /// Where DEPLOYMENT STATE — what Tyanor owns — is kept. Defaults to <c>tyanor/state.json</c> under the
    /// application's base directory: a working default that needs no server, no schema and no decision on
    /// day one. Set it to put state on a shared volume, beside a project, or anywhere an operator can find
    /// it.
    /// </summary>
    /// <remarks>
    /// Ignored when a store is supplied directly via <see cref="TyanorBuilder.UseState(IStateStore)"/>,
    /// named by a descriptor via <see cref="TyanorBuilder.UseState(string)"/>, or replaced by
    /// <see cref="TyanorBuilder.UseInMemoryState"/>.
    /// </remarks>
    public string StatePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "tyanor", "state.json");

    /// <summary>
    /// Where the run LOG is kept — separate from state on purpose, and it does NOT follow
    /// <see cref="StatePath"/>. State is what Tyanor owns and must stay true; history is an append-only
    /// account of attempts. They have different lifetimes, and a team that shares state does not necessarily
    /// want to share every operator's run log. Defaults to <c>tyanor/runs.json</c>.
    /// </summary>
    public string RunHistoryPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "tyanor", "runs.json");

    /// <summary>Retry policy for TRANSIENT provider errors. Credential and hard failures never retry.</summary>
    public RetryPolicy Retry { get; set; } = new();

    /// <summary>The descriptor state was configured from, if one was — for showing an operator where their
    /// state lives without them having to know how the container was wired.</summary>
    public string? StateConnection { get; set; }

    /// <summary>The descriptor the run log was configured from, if one was.</summary>
    public string? HistoryConnection { get; set; }
}

/// <summary>
/// The configuration surface handed to <see cref="TyanorServiceCollectionExtensions.AddTyanor"/> —
/// state location, retry, and the targets this application can deploy to.
/// </summary>
/// <remarks>
/// Targets are registered here rather than discovered on disk. A deployment tool holds credentials and
/// mutates infrastructure; loading code it happened to find is a security question nobody asked for
/// (<c>docs/DECISIONS.md</c> D6).
/// </remarks>
public sealed class TyanorBuilder
{
    private readonly IServiceCollection _services;

    internal TyanorBuilder(IServiceCollection services, TyanorOptions options)
    {
        _services = services;
        Options = options;
    }

    /// <summary>The options being built.</summary>
    public TyanorOptions Options { get; }

    /// <summary>
    /// Keep deployment state — the one set, recording what Tyanor owns — in a JSON file at
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Where the state file lives.</param>
    /// <remarks>
    /// This moves STATE only. The run log stays at <see cref="TyanorOptions.RunHistoryPath"/> until
    /// <see cref="UseFileHistory"/> moves it, because the two are deliberately separate stores with
    /// different lifetimes — see <see cref="TyanorOptions.RunHistoryPath"/>.
    /// </remarks>
    public TyanorBuilder UseFileState(string path)
    {
        Options.StatePath = path;
        _services.AddSingleton<IStateStore>(_ => new FileStateStore(path));
        return this;
    }

    /// <summary>Supply your own state store — S3, Postgres, wherever the one set of state should live.</summary>
    /// <param name="store">The store.</param>
    public TyanorBuilder UseState(IStateStore store)
    {
        _services.AddSingleton(store);
        return this;
    }

    /// <summary>Keep the run LOG in a JSON file at <paramref name="path"/>.</summary>
    /// <param name="path">Where the log lives.</param>
    public TyanorBuilder UseFileHistory(string path)
    {
        Options.RunHistoryPath = path;
        _services.AddSingleton<IRunHistory>(_ => new FileRunHistory(path));
        return this;
    }

    /// <summary>
    /// Keep BOTH the run log and deployment state in memory only. Nothing survives the process, so nothing
    /// can be resumed after a crash and a later teardown cannot tell what Tyanor created from what was
    /// already there — appropriate for tests and one-shot CI runs, and a mistake anywhere an operator would
    /// expect to re-enter a deployment.
    /// </summary>
    /// <remarks>
    /// It registers both on purpose. Registering only the history left state going to a FILE under the
    /// application's base directory, so a method named for memory quietly wrote to disk — the sort of
    /// difference nobody finds until a test run leaves state behind.
    /// </remarks>
    public TyanorBuilder UseInMemoryState()
    {
        _services.AddSingleton<IRunHistory, InMemoryRunHistory>();
        _services.AddSingleton<IStateStore, InMemoryStateStore>();
        return this;
    }

    /// <summary>Supply your own store — SQLite, Postgres, a table in the app's existing database.</summary>
    /// <param name="history">The run log.</param>
    public TyanorBuilder UseHistory(IRunHistory history)
    {
        _services.AddSingleton(history);
        return this;
    }

    /// <summary>
    /// Register a storage backend, so a descriptor can name it — <c>"sqlite:…"</c>, <c>"postgres:…"</c>,
    /// <c>"s3://…"</c>.
    /// </summary>
    /// <param name="backend">Yours, or one from a package. <c>json</c> is registered already.</param>
    /// <remarks>
    /// Nothing is discovered from disk (D6). This is the one line that makes a kind available, and it is the
    /// same line whether the backend came from this repository or from your own application (D20).
    /// </remarks>
    public TyanorBuilder AddStorage(IStorageBackend backend)
    {
        _services.AddSingleton(backend);
        return this;
    }

    /// <summary>
    /// Put deployment state wherever a descriptor says — <c>"{kind}:{target}"</c>, read from configuration
    /// rather than branched on in code.
    /// </summary>
    /// <param name="descriptor">For example <c>"sqlite:/var/lib/myapp/tyanor.db"</c>.</param>
    /// <remarks>
    /// Resolved when the container builds it, not here, so a kind registered later in the same
    /// <c>AddTyanor</c> callback still counts — the order of two lines in a composition root should not
    /// decide whether an application starts.
    /// </remarks>
    public TyanorBuilder UseState(string descriptor)
    {
        Options.StateConnection = descriptor;
        _services.AddSingleton<IStateStore>(sp =>
            sp.GetRequiredService<StorageBackends>().State(descriptor));
        return this;
    }

    /// <summary>Put the run log wherever a descriptor says.</summary>
    /// <param name="descriptor">For example <c>"postgres:Host=db;Database=ops"</c>.</param>
    public TyanorBuilder UseHistory(string descriptor)
    {
        Options.HistoryConnection = descriptor;
        _services.AddSingleton<IRunHistory>(sp =>
            sp.GetRequiredService<StorageBackends>().History(descriptor));
        return this;
    }

    /// <summary>
    /// Register a deployment target this application can run procedures against. Call it once per provider —
    /// they coexist and are selected by <see cref="IDeploymentTarget.Id"/>.
    /// </summary>
    /// <param name="target">The target. Yours or a built-in one; there is no difference here.</param>
    public TyanorBuilder AddTarget(IDeploymentTarget target)
    {
        _services.AddSingleton(target);
        return this;
    }

    /// <summary>Register a target the container constructs, so it can take its own dependencies.</summary>
    /// <typeparam name="T">The target type.</typeparam>
    public TyanorBuilder AddTarget<T>() where T : class, IDeploymentTarget
    {
        _services.AddSingleton<IDeploymentTarget, T>();
        return this;
    }

    /// <summary>Bound how a transient provider error is retried before the run pauses.</summary>
    public TyanorBuilder UseRetry(RetryPolicy retry)
    {
        Options.Retry = retry;
        return this;
    }
}

/// <summary>Registers Tyanor with a DI container.</summary>
public static class TyanorServiceCollectionExtensions
{
    /// <summary>
    /// Add Tyanor: the two stores, the retry policy, and the targets available to this application.
    ///
    /// <code>
    /// services.AddTyanor(cfg =>
    /// {
    ///     cfg.UseState("json:/var/lib/myapp/state.json");     // what Tyanor OWNS — YOUR choice
    ///     cfg.UseHistory("json:/var/lib/myapp/runs.json");    // what was ATTEMPTED
    ///     cfg.AddTarget(new AwsTarget(credentials));
    /// });
    /// </code>
    ///
    /// <para>With neither configured, both go to JSON files under the application's base directory. That is
    /// a real, durable default — an in-memory one would look like it worked right up until the moment
    /// resume mattered.</para>
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Configuration callback.</param>
    /// <exception cref="ArgumentException">
    /// State and the run log were pointed at the SAME location — see <see cref="RefuseOneLocation"/>.
    /// </exception>
    public static IServiceCollection AddTyanor(this IServiceCollection services, Action<TyanorBuilder>? configure = null)
    {
        var options = new TyanorOptions();
        configure?.Invoke(new TyanorBuilder(services, options));
        RefuseOneLocation(options);

        services.TryAddSingleton(options);
        // Only if the caller registered none — TryAdd means an explicit UseFileState/UseInMemoryState/
        // UseHistory above always wins over this fallback.
        services.TryAddSingleton<IRunHistory>(_ => new FileRunHistory(options.RunHistoryPath));
        // Deployment state — what Tyanor owns. Without it the engine still reconciles and resumes, but it
        // cannot say what it created or produce add/change/destroy counts, so the default supplies one.
        services.TryAddSingleton<IStateStore>(_ => new FileStateStore(options.StatePath));

        // The json backend is always available: it needs no package, no server and no decision on day one.
        // TryAddEnumerable so registering it twice — here and explicitly — does not produce two.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStorageBackend, JsonStorageBackend>());
        services.TryAddSingleton(sp => new StorageBackends(sp.GetServices<IStorageBackend>()));

        // EVERY registered target, keyed by id. Resolving IDeploymentTarget directly returns whichever was
        // registered last, which is fine at one provider and a wrong deployment at two.
        services.TryAddSingleton(sp => new DeploymentTargets(sp.GetServices<IDeploymentTarget>()));
        services.TryAddSingleton(sp => new ProcedureRunners(
            sp.GetRequiredService<DeploymentTargets>(),
            sp.GetRequiredService<IRunHistory>(),
            sp.GetService<IStateStore>(),
            sp.GetRequiredService<TyanorOptions>().Retry));

        // A bare ProcedureRunner stays resolvable, because one target is the ordinary case and asking for a
        // runner should not require knowing about a factory. With several registered this throws and NAMES
        // them, rather than silently picking one — see DeploymentTargets.Single.
        services.TryAddSingleton(sp => sp.GetRequiredService<ProcedureRunners>().ForSingle());
        return services;
    }

    /// <summary>
    /// Refuse a configuration that points state and the run log at ONE location.
    /// </summary>
    /// <remarks>
    /// <para>They hold different shapes — a list of deployments and a list of run records — so sharing a file
    /// does not fail loudly. Each store reads the other's contents as its own type, gets a list of records
    /// with every field defaulted, and writes that back. The result is two stores quietly destroying each
    /// other's data, with the deployment state going first, which is the one that answers what a teardown is
    /// allowed to remove.</para>
    /// <para>Caught here rather than left to be discovered, for the reason <c>docs/DECISIONS.md</c> D20 gives
    /// about refusing a bare path: a deployment tool asking rather than guessing is worth one clear error.</para>
    /// <para>It checks the two SAME-KIND pairs — two descriptors, or two paths. A descriptor and a path that
    /// happen to name one file are not compared, because comparing them means resolving a descriptor whose
    /// target only its backend can interpret, and Core knowing what a Postgres connection string means is the
    /// leak D4 is about.</para>
    /// </remarks>
    /// <param name="options">The configured options.</param>
    /// <exception cref="ArgumentException">Both stores were given the same location.</exception>
    private static void RefuseOneLocation(TyanorOptions options)
    {
        Refuse(options.StateConnection, options.HistoryConnection, "descriptor");
        Refuse(options.StatePath, options.RunHistoryPath, "path");

        static void Refuse(string? state, string? history, string what)
        {
            if (state is null || !string.Equals(state, history, StringComparison.OrdinalIgnoreCase)) return;

            throw new ArgumentException(
                $"Deployment state and the run log are both configured at the {what} '{state}'. They hold " +
                "different things and would overwrite each other — state is what Tyanor OWNS and must stay " +
                "true, the run log is an account of what was attempted. Give them separate locations.");
        }
    }
}

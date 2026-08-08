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
    /// Where run state is kept. Defaults to <c>tyanor/runs.json</c> under the application's base
    /// directory — a working default that needs no server, no schema and no decision on day one.
    /// Set it to put state on a shared volume, beside a project, or anywhere an operator can find it.
    /// </summary>
    /// <remarks>
    /// Ignored when a history is supplied directly via <see cref="TyanorBuilder.UseHistory"/> or
    /// <see cref="TyanorBuilder.UseInMemoryState"/>.
    /// </remarks>
    public string StatePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "tyanor", "state.json");

    /// <summary>
    /// Where the run LOG is kept — separate from state on purpose. State is what Tyanor owns and must stay
    /// true; history is an append-only account of attempts. They have different lifetimes, and a team that
    /// shares state does not necessarily want to share every operator's run log.
    /// </summary>
    public string RunHistoryPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "tyanor", "runs.json");

    /// <summary>Retry policy for TRANSIENT provider errors. Credential and hard failures never retry.</summary>
    public RetryPolicy Retry { get; set; } = new();
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
    /// <paramref name="path"/>. Run history goes beside it unless <see cref="UseFileHistory"/> says otherwise.
    /// </summary>
    public TyanorBuilder UseFileState(string path)
    {
        Options.StatePath = path;
        _services.AddSingleton<IStateStore>(_ => new FileStateStore(path));
        return this;
    }

    /// <summary>Supply your own state store — S3, Postgres, wherever the one set of state should live.</summary>
    public TyanorBuilder UseState(IStateStore store)
    {
        _services.AddSingleton(store);
        return this;
    }

    /// <summary>Keep the run LOG in a JSON file at <paramref name="path"/>.</summary>
    public TyanorBuilder UseFileHistory(string path)
    {
        Options.RunHistoryPath = path;
        _services.AddSingleton<IRunHistory>(_ => new FileRunHistory(path));
        return this;
    }

    /// <summary>
    /// Keep run state in memory only. Nothing survives the process, so nothing can be resumed after a
    /// crash — appropriate for tests and one-shot CI runs, and a mistake anywhere an operator would expect
    /// to re-enter a deployment.
    /// </summary>
    public TyanorBuilder UseInMemoryState()
    {
        _services.AddSingleton<IRunHistory, InMemoryRunHistory>();
        return this;
    }

    /// <summary>Supply your own store — SQLite, Postgres, a table in the app's existing database.</summary>
    public TyanorBuilder UseHistory(IRunHistory history)
    {
        _services.AddSingleton(history);
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
    /// Add Tyanor: run state, retry policy, and the targets available to this application.
    ///
    /// <code>
    /// services.AddTyanor(cfg =>
    /// {
    ///     cfg.UseFileState("/var/lib/myapp/runs.json");   // where run state lives — YOUR choice
    ///     cfg.AddTarget(new AwsTarget(credentials));
    /// });
    /// </code>
    ///
    /// <para>With no state configured, run history goes to a JSON file under the application's base
    /// directory. That is a real, durable default — an in-memory one would look like it worked right up
    /// until the moment resume mattered.</para>
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Configuration callback.</param>
    public static IServiceCollection AddTyanor(this IServiceCollection services, Action<TyanorBuilder>? configure = null)
    {
        var options = new TyanorOptions();
        configure?.Invoke(new TyanorBuilder(services, options));

        services.TryAddSingleton(options);
        // Only if the caller registered none — TryAdd means an explicit UseFileState/UseInMemoryState/
        // UseHistory above always wins over this fallback.
        services.TryAddSingleton<IRunHistory>(_ => new FileRunHistory(options.RunHistoryPath));
        // Deployment state — what Tyanor owns. Without it the engine still reconciles and resumes, but it
        // cannot say what it created or produce add/change/destroy counts, so the default supplies one.
        services.TryAddSingleton<IStateStore>(_ => new FileStateStore(options.StatePath));

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
}

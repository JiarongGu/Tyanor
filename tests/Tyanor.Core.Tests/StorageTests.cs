using Microsoft.Extensions.DependencyInjection;
using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Storage named by a descriptor — <c>"{kind}:{target}"</c> — so where state lives is configuration rather
/// than a branch in code.
/// </summary>
public class StorageConnectionTests
{
    [Theory]
    [InlineData("json:/var/lib/myapp/state.json", "json", "/var/lib/myapp/state.json")]
    [InlineData("sqlite:/var/lib/myapp/tyanor.db", "sqlite", "/var/lib/myapp/tyanor.db")]
    [InlineData("postgres:Host=db;Database=ops;Username=tyanor", "postgres", "Host=db;Database=ops;Username=tyanor")]
    [InlineData("s3://my-bucket/tyanor/state.json", "s3", "my-bucket/tyanor/state.json")]
    public void A_descriptor_splits_into_a_kind_and_everything_after_it(string descriptor, string kind, string target)
    {
        // The target is not parsed further, on purpose: a connection string full of semicolons and equals
        // signs is the backend's business and nobody else's.
        var connection = StorageConnection.Parse(descriptor);

        Assert.Equal(kind, connection.Kind);
        Assert.Equal(target, connection.Target);
    }

    [Fact]
    public void A_WINDOWS_PATH_is_not_mistaken_for_a_kind()
    {
        // The one that would bite first, since it is the most likely thing anyone types. A kind must be at
        // least two characters — the same rule URI parsers use to tell a scheme from a drive letter.
        var connection = StorageConnection.Parse(@"json:C:\ProgramData\myapp\state.json");

        Assert.Equal("json", connection.Kind);
        Assert.Equal(@"C:\ProgramData\myapp\state.json", connection.Target);

        Assert.Throws<ArgumentException>(() => StorageConnection.Parse(@"C:\ProgramData\myapp\state.json"));
    }

    [Theory]
    [InlineData("/var/lib/myapp/state.json")]        // a bare path names no kind
    [InlineData("state.json")]
    [InlineData("sqlite")]                           // a kind and no location
    [InlineData("sqlite:")]
    [InlineData(":/var/lib/state.json")]
    [InlineData("")]
    public void An_incomplete_descriptor_is_refused_rather_than_guessed(string descriptor)
        // A bare path would be convenient and has to guess. A typo like "sqlite/state.db" would be read as a
        // FILE called sqlite/state.db and would silently write state somewhere nobody meant — which is worth
        // more than the one extra word costs.
        => Assert.Throws<ArgumentException>(() => StorageConnection.Parse(descriptor));

    [Fact]
    public void A_bare_path_is_refused_with_the_fix_in_the_message()
    {
        var error = Assert.Throws<ArgumentException>(() => StorageConnection.Parse("/var/lib/state.json"));

        Assert.Contains("json:/var/lib/state.json", error.Message);
    }

    [Fact]
    public void A_descriptor_round_trips()
        => Assert.Equal("sqlite:/var/lib/tyanor.db",
            StorageConnection.Parse("sqlite:/var/lib/tyanor.db").ToString());
}

/// <summary>The registry that turns a descriptor into a store.</summary>
public class StorageBackendsTests
{
    [Fact]
    public void A_descriptor_opens_the_backend_it_names()
    {
        var backends = new StorageBackends(new JsonStorageBackend(), new FakeBackend("sqlite"));

        Assert.IsType<FileStateStore>(backends.State("json:state.json"));
        Assert.IsType<FakeBackend.Store>(backends.State("sqlite:/var/lib/tyanor.db"));
    }

    [Fact]
    public void A_kind_nobody_registered_says_what_IS_registered_and_how_to_add_one()
    {
        // "unknown kind sqlite" with no further help is a support conversation waiting to happen.
        var backends = new StorageBackends(new JsonStorageBackend());

        var error = Assert.Throws<ArgumentException>(() => backends.State("postgres:Host=db"));

        Assert.Contains("postgres", error.Message);
        Assert.Contains("json", error.Message);                  // …what you DO have
        Assert.Contains("IStorageBackend", error.Message);        // …and how to bring your own
    }

    [Fact]
    public void A_kind_is_matched_however_it_is_cased()
        => Assert.NotNull(new StorageBackends(new JsonStorageBackend()).State("JSON:state.json"));

    [Fact]
    public void Two_backends_claiming_one_kind_is_refused()
        => Assert.Throws<ArgumentException>(() =>
            new StorageBackends(new FakeBackend("sqlite"), new FakeBackend("sqlite")));

    [Fact]
    public void A_backend_with_no_kind_is_refused_because_no_descriptor_could_ask_for_it()
        => Assert.Throws<ArgumentException>(() => new StorageBackends(new FakeBackend("  ")));

    [Fact]
    public void A_backend_that_cannot_hold_one_of_the_two_says_so()
    {
        // Better than returning something that quietly loses writes.
        var backends = new StorageBackends(new StateOnlyBackend());

        Assert.NotNull(backends.State("stateonly:x"));
        Assert.Throws<NotSupportedException>(() => backends.History("stateonly:x"));
    }

    internal sealed class FakeBackend(string kind) : IStorageBackend
    {
        public string Kind => kind;
        public IStateStore OpenState(StorageConnection connection) => new Store();
        public IRunHistory OpenHistory(StorageConnection connection) => new InMemoryRunHistory();

        internal sealed class Store : IStateStore
        {
            public Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default) =>
                Task.FromResult(DeploymentState.Empty(procedure, prefix));
            public Task SaveAsync(DeploymentState state, CancellationToken ct = default) => Task.CompletedTask;
            public Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    private sealed class StateOnlyBackend : IStorageBackend
    {
        public string Kind => "stateonly";
        public IStateStore OpenState(StorageConnection connection) => new FakeBackend.Store();
        public IRunHistory OpenHistory(StorageConnection connection) =>
            throw new NotSupportedException("This backend holds state only; put the run log somewhere else.");
    }
}

/// <summary>The json backend, held to the same contracts every other one will be.</summary>
public class JsonStorageBackendTests : IDisposable
{
    private readonly Scratch _scratch = new();

    private StorageConnection Connection() => StorageConnection.Parse($"json:{_scratch.Path("via-descriptor")}");

    public static TheoryData<string> StateChecks() =>
        FileRunHistoryContractTests.Names(new StateStoreContract(() => null!));

    public static TheoryData<string> HistoryChecks() =>
        FileRunHistoryContractTests.Names(new RunHistoryContract(() => null!));

    [Theory]
    [MemberData(nameof(StateChecks))]
    public Task State_opened_from_a_descriptor_satisfies_the_contract(string check) =>
        new StateStoreContract(() => new JsonStorageBackend().OpenState(Connection())).AssertAsync(check);

    [Theory]
    [MemberData(nameof(HistoryChecks))]
    public Task History_opened_from_a_descriptor_satisfies_the_contract(string check) =>
        new RunHistoryContract(() => new JsonStorageBackend().OpenHistory(Connection())).AssertAsync(check);

    [Fact]
    public void A_relative_target_resolves_against_the_application_not_the_working_directory()
    {
        // A deployment tool whose state moves when someone runs it from a different folder is a deployment
        // tool that loses state.
        var store = new JsonStorageBackend().OpenState(StorageConnection.Parse("json:tyanor/state.json"));

        Assert.StartsWith(AppContext.BaseDirectory, Assert.IsType<FileStateStore>(store).Path);
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>Wiring storage by descriptor through a container.</summary>
public class StorageCompositionTests
{
    [Fact]
    public void A_descriptor_in_configuration_is_all_it_takes()
    {
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg.UseState("json:state.json").UseHistory("json:runs.json"));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileStateStore>(provider.GetRequiredService<IStateStore>());
        Assert.IsType<FileRunHistory>(provider.GetRequiredService<IRunHistory>());
        Assert.Equal("json:state.json", provider.GetRequiredService<TyanorOptions>().StateConnection);
    }

    [Fact]
    public void A_backend_registered_AFTER_the_descriptor_still_counts()
    {
        // Resolution happens when the container builds the store, not when the line is written. The order of
        // two lines in a composition root should not decide whether an application starts.
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg
            .UseState("sqlite:/var/lib/tyanor.db")
            .AddStorage(new StorageBackendsTests.FakeBackend("sqlite")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<StorageBackendsTests.FakeBackend.Store>(provider.GetRequiredService<IStateStore>());
    }

    [Fact]
    public void The_json_backend_is_there_without_anyone_registering_it()
        // It needs no package, no server and no decision on day one, so it is the one that can be a default.
        => Assert.Contains("json",
            new ServiceCollection().AddTyanor().BuildServiceProvider()
                .GetRequiredService<StorageBackends>().Kinds);

    [Fact]
    public void An_unregistered_kind_fails_when_the_store_is_resolved_and_names_the_kinds_available()
    {
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg.UseState("postgres:Host=db"));

        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<ArgumentException>(provider.GetRequiredService<IStateStore>);
        Assert.Contains("postgres", error.Message);
    }
}

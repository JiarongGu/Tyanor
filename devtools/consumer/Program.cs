using Tyanor;
using Tyanor.Testing;

// The stranger's project: this file is COPIED out of the repository, built against the packed .nupkg, and
// run. It may only use the public surface — that is the whole point of it.
//
// It checks the things this repository structurally CANNOT check about itself:
//
//   · the package restores and a consumer project compiles against it at all
//   · a DEFAULT INTERFACE MEMBER is really called across an assembly boundary, for somebody else's class.
//     C# fixes an interface mapping at the class naming the interface, so a member that looks overridden
//     can compile and silently never run. Every fixture and target in the repository implements its
//     interface directly, so the trap cannot fire there. It fired here on the first attempt (D32, D39).
//   · the contract suites can go RED for a stranger's broken driver. A suite that passes a correct
//     implementation and cannot fail a broken one certifies nothing.
//   · the documented exception bases are catchable, which is how a consumer tells "you configured this
//     wrongly" from "the cloud said no" without matching on message text.
//
// Add a case when a release adds a seam. Do not delete one: they are cheap, and each is here because
// something was once wrong.

var failed = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  {what}{(detail.Length > 0 ? $"   [{detail}]" : "")}");
    if (!ok) failed++;
}

var nothing = new DeploymentArtifact(new Dictionary<string, string>());
var web = new ProcedureUnit("web", "Website");

Console.WriteLine("\nconfiguration is refused in the consumer's own words");

try
{
    new DeploymentRequest("acme", nothing, new Dictionary<string, string> { ["bucket"] = "shared" })
        .Address("web", "bucket");
    Check("an address written procedure-wide is refused", false, "it returned instead");
}
catch (OptionException e)
{
    Check("an address written procedure-wide is refused", true);
    Check("…naming the spelling that works", e.Message.Contains("\"web.bucket\""));
    Check("…and catchable as DefinitionException", e is DefinitionException);
}

Check("a unit's own address is read",
    new DeploymentRequest("acme", nothing, new Dictionary<string, string> { ["web.bucket"] = "mine" })
        .Address("web", "bucket") == "mine");

Console.WriteLine("\ndefault interface members, across an assembly boundary");

IUnitDriverFixture inherited = new PlainFixture();
Check("a fixture declaring nothing gets the DEFAULT",
    inherited.Elsewhere?.Prefix == "contract-2", inherited.Elsewhere?.Prefix ?? "null");

IUnitDriverFixture direct = new DirectFixture();
Check("a fixture declaring it ON the implementing class is the one called",
    direct.Elsewhere?.Prefix == "declared-directly", direct.Elsewhere?.Prefix ?? "null");

IDeploymentTarget swept = new SweepingTarget();
await swept.SweepAsync(new SweepContext("site", new DeploymentRequest("acme", nothing), _ => { }, default));
Check("a target's own SweepAsync overrides the default and RUNS", SweepingTarget.Swept);

Console.WriteLine("\nthe contract suites certify, and can go red");

var good = await new UnitDriverContract(new PlainFixture()).RunAllAsync();
Check("a correct driver passes every check",
    good.All(c => c.Passed), string.Join(" | ", good.Where(c => !c.Passed).Select(c => c.Name)));

var broken = await new UnitDriverContract(new BrokenFixture()).RunAllAsync();
Check("a driver that ignores the prefix FAILS the isolation checks",
    broken.Count(c => !c.Passed) >= 2, $"{broken.Count(c => !c.Passed)} failed");

// A fixture whose own answer is silently ignored — the defect the published 0.2.0 revealed (D39).
var shadowed = await new UnitDriverContract(new ShadowingFixture()).RunAllAsync();
Check("a fixture whose own answers are ignored is REPORTED",
    shadowed.Any(c => !c.Passed && c.Name.Contains("fixture's own answers")),
    string.Join(" | ", shadowed.Where(c => !c.Passed).Select(c => c.Name)));

Console.WriteLine($"\n{(failed == 0 ? "all pass" : $"{failed} FAILED")} — from the packed artifact\n");
return failed;

// ── a stranger's driver ──────────────────────────────────────────────────────────────────────────
file class Site : StepUnitDriver
{
    protected readonly HashSet<string> Live = new(StringComparer.Ordinal);

    protected virtual string Key(UnitContext c) => $"{c.Request.Prefix}/{c.Name}";

    public override Task<UnitPhase> PhaseAsync(UnitContext c) =>
        Task.FromResult(Live.Contains(Key(c)) ? UnitPhase.Ready : UnitPhase.Missing);

    public override Task CreateAsync(UnitContext c)
    {
        c.Address("bucket");                        // resolves the address, refusing an unscoped one
        Live.Add(Key(c));
        return Task.CompletedTask;
    }

    public override Task RemoveAsync(UnitContext c)
    {
        Live.Remove(Key(c));
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
        Task.FromResult<IReadOnlyList<ResourceState>>(
            Live.Contains(Key(c)) ? [new ResourceState($"site://{Key(c)}", "site/bucket", "v1")] : []);

    public override Task<IReadOnlyList<string>> ValidateAsync(UnitContext c) =>
        new UnitProblems().Check(() => c.Address("bucket")).Found();
}

/// <summary>The prefix left out of the key — what the isolation checks exist to catch.</summary>
file sealed class Broken : Site
{
    protected override string Key(UnitContext c) => c.Name;
}

file class PlainFixture : IUnitDriverFixture
{
    private readonly Site _site = new();
    public virtual IUnitDriver Driver => _site;
    public ProcedureUnit Unit { get; } = new("web", "Website");
    public DeploymentRequest Request { get; } = new("contract",
        new DeploymentArtifact(new Dictionary<string, string>()),
        new Dictionary<string, string> { ["web.bucket"] = "contract-bucket" });
    public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
}

file sealed class DirectFixture : IUnitDriverFixture
{
    private readonly Site _site = new();
    public IUnitDriver Driver => _site;
    public ProcedureUnit Unit { get; } = new("web", "Website");
    public DeploymentRequest Request { get; } = new("contract",
        new DeploymentArtifact(new Dictionary<string, string>()),
        new Dictionary<string, string> { ["web.bucket"] = "b" });
    public DeploymentRequest? Elsewhere => Request with { Prefix = "declared-directly" };
    public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
}

/// <summary>Declares it on a DERIVED class, where the mapping was already fixed. Silently ignored (D39).</summary>
file sealed class ShadowingFixture : PlainFixture
{
    public DeploymentRequest? Elsewhere => Request with { Prefix = "never-called" };
}

file sealed class BrokenFixture : PlainFixture
{
    private readonly Broken _broken = new();
    public override IUnitDriver Driver => _broken;
}

/// <summary>A target of the consumer's own, overriding the defaulted sweep.</summary>
file sealed class SweepingTarget : IDeploymentTarget
{
    public static bool Swept { get; private set; }

    public string Id => "stranger";

    public IUnitDriver Driver { get; } = new Site();

    public IFailureClassifier Classifier { get; } = new Never();

    public Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct = default) =>
        Task.FromResult(new TargetIdentity(true, "account", "principal"));

    public Task SweepAsync(SweepContext context)
    {
        Swept = true;
        return Task.CompletedTask;
    }

    private sealed class Never : IFailureClassifier
    {
        public FailureClass? Classify(Exception error) => null;
    }
}

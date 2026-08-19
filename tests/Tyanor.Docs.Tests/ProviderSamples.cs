using Tyanor;
using Tyanor.Engine;
using Tyanor.Providers.Aws;
using Tyanor.Providers.Local;

namespace Tyanor.Docs.Tests;

/// <summary>
/// Every C# sample in <c>docs/providers.md</c>, compiled.
///
/// <para><b>Why this file exists.</b> The provider reference is the page an adopter keeps open while
/// writing a request, and it is almost entirely option NAMES — the part of an API that a rename breaks
/// silently, because nothing about a string in a document stops compiling. So the two samples that carry
/// those names are written against the real constants and built on every run.</para>
///
/// <para><b>The samples are not duplicated here; they are the SAME TEXT.</b> <c>npm run doctor</c> refuses
/// any fence in that document which does not appear in this file, ignoring indentation.</para>
///
/// <para>Nothing here is executed. These are compile-time assertions about signatures and constants, and
/// running them would deploy to <c>/srv</c> and to somebody's AWS account.</para>
/// </summary>
internal static class ProviderSamples
{
    // ── the two targets, as the reference introduces them ────────────────────────────────────────
    private static void TheLocalTarget()
    {
        var target = new LocalTarget("/srv");            // Id "local"

        _ = target;
    }

    private static void TheAwsTarget()
    {
        using var target = new AwsTarget(
            new TargetCredentials("AKIA…", "…", Region: "ap-southeast-2"));    // Id "aws"

        _ = target;
    }

    // ── a complete request, per provider ─────────────────────────────────────────────────────────
    private static async Task AServerOnThisMachine(
        string publishOutput, IRunHistory history, IStateStore state)
    {
        var procedure = new Procedure("server",
        [
            new ProcedureUnit("runtime", "Application files"),
            new ProcedureUnit("service", "Server", Weight: 3),
        ]);

        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = publishOutput }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "app",

                ["service.kind"] = LocalOptions.ProcessKind,
                ["service.command"] = "dotnet",
                ["service.args"] = "Server.dll --urls http://localhost:8080",
                ["service.watch"] = "runtime",              // restart when runtime's content moves
                ["service.health.port"] = "8080",
                ["service.health.seconds"] = "90",          // a slow first boot, said out loud
            });

        var runner = new ProcedureRunner(new LocalTarget("/srv"), history, state);
        await runner.ApplyAsync(procedure, request, Console.WriteLine);
    }

    private static async Task ASiteOnAws(
        TargetCredentials credentials, IRunHistory history, IStateStore state)
    {
        var procedure = new Procedure("site",
        [
            new ProcedureUnit("db", "Database", Weight: 4),
            new ProcedureUnit("api", "API", Weight: 3),
            new ProcedureUnit("web", "Website"),
            new ProcedureUnit("content", "Website files"),
        ]);

        var request = new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string>
            {
                ["db-template"] = "bundle/mysite-db.template.json",
                ["api-template"] = "bundle/mysite-api.template.json",
                ["web-template"] = "bundle/mysite-web.template.json",
                ["lambda"] = "bundle/assets",
                ["site"] = "dist/web",
            }),
            new Dictionary<string, string>
            {
                ["kind"] = AwsOptions.StackKind,            // all but one unit, so it is written once

                ["db.template"] = "db-template",
                ["db.parameter.InstanceClass"] = "db.t4g.micro",

                ["api.template"] = "api-template",
                ["api.assets"] = "lambda",                  // the Lambda zips the template refers to

                ["web.template"] = "web-template",

                ["content.kind"] = AwsOptions.ContentKind,  // the exception
                ["content.source"] = "site",
                ["content.bucketFrom"] = "web:webbucketname",
                ["content.invalidateFrom"] = "web:distributionid",
            },
            Tags: new Dictionary<string, string> { ["Application"] = "mysite" });

        using var aws = new AwsTarget(credentials);
        var runner = new ProcedureRunner(aws, history, state);

        var plan = await runner.PlanAsync(procedure, request);
        if (!plan.IsDestructive) await runner.ApplyAsync(procedure, request, Console.WriteLine);
    }
}

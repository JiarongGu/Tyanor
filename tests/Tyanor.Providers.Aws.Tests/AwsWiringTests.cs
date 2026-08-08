using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The parts of the provider that are decided before any AWS call happens — which is where a misconfigured
/// deployment should be refused, rather than three units into a run that has already created things.
/// </summary>
public class AwsWiringTests
{
    private static readonly ProcedureUnit Api = new("api", "API");

    private static AwsTarget Target() =>
        new(new TargetCredentials("AKIAEXAMPLE", "secret", "ap-southeast-2"));

    private static DeploymentRequest Request(Dictionary<string, string> options) =>
        new("mysite", new DeploymentArtifact(new Dictionary<string, string>()), options);

    [Fact]
    public void A_region_is_required_because_defaulting_one_deploys_somewhere_nobody_named()
    {
        Assert.Throws<ArgumentException>(() => new AwsTarget(new TargetCredentials("k", "s")));
        Assert.Throws<ArgumentException>(() => new AwsTarget(new TargetCredentials("k", "s", "  ")));
    }

    [Fact]
    public async Task A_unit_that_does_not_say_what_it_is_fails_before_anything_is_called()
    {
        using var target = Target();

        var error = await Assert.ThrowsAsync<UnitKindException>(
            () => target.Driver.PhaseAsync(Api, Request([]), default));

        Assert.Contains(AwsOptions.StackKind, error.Message);        // and says what the choices are
        Assert.Contains(AwsOptions.ContentKind, error.Message);
    }

    [Fact]
    public async Task A_kind_this_provider_does_not_have_fails_rather_than_defaulting_to_stack()
    {
        // Defaulting would deploy CloudFormation against a template the operator never named.
        using var target = Target();
        var request = Request(new Dictionary<string, string> { ["api.kind"] = "lambda" });

        var error = await Assert.ThrowsAsync<UnitKindException>(
            () => target.Driver.PhaseAsync(Api, request, default));

        Assert.Contains("lambda", error.Message);
    }

    [Fact]
    public async Task A_template_part_the_artifact_does_not_carry_fails_before_a_bucket_is_touched()
    {
        using var target = Target();
        var request = new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string> { ["built"] = "/somewhere" }),
            new Dictionary<string, string>
            {
                ["api.kind"] = AwsOptions.StackKind,
                ["api.template"] = "the-one-nobody-built",
            });

        var error = await Assert.ThrowsAsync<ArtifactException>(
            () => target.Driver.CreateAsync(Api, request, default));

        Assert.Contains("built", error.Message);                     // says what the artifact DOES carry
    }

    [Fact]
    public async Task Everything_wrong_with_a_DEFINITION_is_catchable_as_one_thing()
    {
        // What a consumer showing this to a person actually needs: "you configured this wrongly, fix it and
        // nothing is lost" is a different screen from "CloudFormation rolled your stack back", and telling
        // them apart must not require matching on message text.
        using var target = Target();

        Assert.IsAssignableFrom<DefinitionException>(await Record.ExceptionAsync(
            () => target.Driver.PhaseAsync(Api, Request([]), default)));

        Assert.IsAssignableFrom<DefinitionException>(await Record.ExceptionAsync(
            () => target.Driver.CreateAsync(Api, Request(new Dictionary<string, string>
            {
                ["api.kind"] = AwsOptions.StackKind,        // a stack that names no template
            }), default)));
    }

    [Fact]
    public void A_definition_error_is_not_classified_which_makes_the_engine_fail_the_run()
    {
        // Providers do not have to know about Core's exceptions. Returning null is correct and the engine's
        // default for null is Hard — which is exactly what a wrong definition is.
        using var target = Target();

        var unrecognised = target.Classifier.Classify(new AwsConfigurationException("no template"));

        Assert.Null(unrecognised);
        Assert.False(OperationOutcome.From(unrecognised ?? FailureClass.Hard).Resumable);
    }

    [Fact]
    public void Stack_parameters_are_authored_one_line_each_and_scoped_to_their_unit()
    {
        // CloudFormation parameters are a SET whose keys a provider cannot know in advance, which is what
        // OptionSet exists for. Encoding them into one value would re-invent a serialization format inside
        // a string.
        var request = Request(new Dictionary<string, string>
        {
            ["parameter.Stage"] = "prod",                            // every unit
            ["api.parameter.MemorySize"] = "512",                    // just this one
            ["api.parameter.Stage"] = "canary",                      // …overriding the shared value
            ["web.parameter.MemorySize"] = "128",                    // another unit's, must not leak in
        });

        var parameters = request.OptionSet("api", AwsOptions.ParameterPrefix);

        Assert.Equal(2, parameters.Count);
        Assert.Equal("canary", parameters["Stage"]);
        Assert.Equal("512", parameters["MemorySize"]);
    }

    [Fact]
    public void Capabilities_default_to_the_ones_every_stack_with_compute_needs()
    {
        // Anything that creates an IAM role needs them, which in practice is every stack worth deploying.
        // Omitting the default would fail every first deploy with a message about capabilities.
        Assert.Contains("CAPABILITY_IAM", AwsOptions.DefaultCapabilities);
        Assert.Contains("CAPABILITY_NAMED_IAM", AwsOptions.DefaultCapabilities);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("web:")]
    [InlineData(":webbucketname")]
    public async Task A_malformed_cross_unit_reference_says_what_the_shape_should_be(string reference)
    {
        // "{unit}:{OutputKey}" is how a content unit finds the bucket a stack made. Getting it wrong must
        // not read as "no bucket configured", which would be a much more confusing failure.
        using var target = Target();
        var request = Request(new Dictionary<string, string>
        {
            ["content.kind"] = AwsOptions.ContentKind,
            ["content.bucketFrom"] = reference,
        });

        var error = await Assert.ThrowsAsync<AwsConfigurationException>(
            () => target.Driver.PhaseAsync(new ProcedureUnit("content", "Website files"), request, default));

        Assert.Contains(AwsOptions.BucketFrom, error.Message);
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("app.js", "application/javascript")]
    [InlineData("styles.css", "text/css")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("site.webmanifest", "application/manifest+json")]
    [InlineData("engine.wasm", "application/wasm")]
    public void Website_files_get_the_type_that_makes_a_browser_render_them(string file, string expected)
        // Not polish: S3 defaults every upload to application/octet-stream, and a page uploaded that way is
        // downloaded rather than displayed. A site deployed without this does not work at all.
        => Assert.Equal(expected, ContentTypes.Of(file));

    [Fact]
    public void An_unknown_extension_falls_back_to_bytes_rather_than_guessing()
        => Assert.Equal("application/octet-stream", ContentTypes.Of("data.unknown"));
}

using RuleHealth.Cli;
using RuleHealth.Core;

namespace RuleHealth.Tests;

public sealed class AppSettingsTests
{
    private static AppSettings From(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return AppSettings.FromEnvironment(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void Defaults_are_for_running_from_the_repository()
    {
        var settings = From();

        Assert.Equal("data", settings.DataDir);
        Assert.Equal("http://localhost:5180/", settings.Listen);
        Assert.Equal("http://localhost:5180", settings.PublicUrl);
        Assert.True(settings.UpdateCheck);
        Assert.Equal("samples", settings.SamplesDir);
        Assert.Null(settings.UiDir);
        Assert.Equal("info", settings.LogLevel);
        Assert.Equal(5180, settings.ListenPort);
    }

    [Fact]
    public void Container_values_are_read()
    {
        // The ENV block of the Dockerfile.
        var settings = From(
            ("RULEHEALTH_DATA", "/data"),
            ("RULEHEALTH_SAMPLES", "/app/samples"),
            ("RULEHEALTH_LISTEN", "http://+:5180/"),
            ("RULEHEALTH_PUBLIC_URL", "http://localhost:5180"),
            ("RULEHEALTH_UPDATE_CHECK", "true"),
            ("RULEHEALTH_UI", "/app/ui"),
            ("RULEHEALTH_LOG_LEVEL", "debug"));

        Assert.Equal("/data", settings.DataDir);
        Assert.Equal("/app/samples", settings.SamplesDir);
        Assert.Equal("http://+:5180/", settings.Listen);
        Assert.Equal(5180, settings.ListenPort);
        Assert.Equal("/app/ui", settings.UiDir);
        Assert.True(settings.Debug);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void Update_check_switch(string value, bool expected)
    {
        Assert.Equal(expected, From(("RULEHEALTH_UPDATE_CHECK", value)).UpdateCheck);
    }

    [Fact]
    public void Empty_values_mean_default()
    {
        var settings = From(("RULEHEALTH_DATA", "  "), ("RULEHEALTH_UPDATE_CHECK", ""));

        Assert.Equal("data", settings.DataDir);
        Assert.True(settings.UpdateCheck);
    }

    [Fact]
    public void Listen_prefix_gets_its_trailing_slash_and_public_url_loses_it()
    {
        var settings = From(("RULEHEALTH_LISTEN", "http://127.0.0.1:5180"), ("RULEHEALTH_PUBLIC_URL", "http://localhost:5180/"));

        Assert.Equal("http://127.0.0.1:5180/", settings.Listen);
        Assert.Equal("http://localhost:5180", settings.PublicUrl);
    }

    [Theory]
    [InlineData("RULEHEALTH_LISTEN", "localhost:5180")]           // no scheme
    [InlineData("RULEHEALTH_LISTEN", "https://localhost:5180/")]  // HttpListener over TLS is not supported here
    [InlineData("RULEHEALTH_LISTEN", "http://localhost/")]        // port is required
    [InlineData("RULEHEALTH_LISTEN", "http://localhost:5180/ui/")]
    [InlineData("RULEHEALTH_PUBLIC_URL", "localhost:5180")]
    [InlineData("RULEHEALTH_PUBLIC_URL", "http://localhost:5180/app")]
    [InlineData("RULEHEALTH_UPDATE_CHECK", "maybe")]
    [InlineData("RULEHEALTH_LOG_LEVEL", "trace")]
    public void Invalid_values_name_the_variable(string name, string value)
    {
        var ex = Assert.Throws<RuleHealthException>(() => From((name, value)));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Data_option_wins_over_the_environment()
    {
        var settings = From(("RULEHEALTH_DATA", "/data"));

        Assert.Equal("/data", CliArgs.Parse(new[] { "serve" }, settings).DataDirectory);
        Assert.Equal("samples", CliArgs.Parse(new[] { "serve", "--data", "samples" }, settings).DataDirectory);
    }

    [Fact]
    public void Debug_log_level_turns_on_verbose()
    {
        Assert.True(CliArgs.Parse(new[] { "collect" }, From(("RULEHEALTH_LOG_LEVEL", "debug"))).Verbose);
        Assert.False(CliArgs.Parse(new[] { "collect" }, From()).Verbose);
        Assert.True(CliArgs.Parse(new[] { "collect", "--verbose" }, From()).Verbose);
    }

    [Fact]
    public void Version_is_semver_without_the_commit_suffix()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppInfo.Version);
    }
}

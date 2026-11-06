using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class CommandLineConfigurationTests
{
    [Test]
    [Arguments("--log-level", "debug", "--log-format", "json")]
    [Arguments("--log-level=debug", "--log-format=json", null, null)]
    public async Task LogOptions_BindToLoggingSection(string a, string b, string? c, string? d)
    {
        // 回帰: 以前は switch mapping が無く "log-level" というキーになり、--log-level が一切効いていなかった
        string[] args = ["テキスト", a, b, .. (c is null ? Array.Empty<string>() : [c, d!])];

        var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(args);

        await Assert.That(configuration["Logging:Level"]).IsEqualTo("debug");
        await Assert.That(configuration["Logging:Format"]).IsEqualTo("json");
    }

    [Test]
    [Arguments("--init")]
    [Arguments("--clear")]
    [Arguments("--test")]
    public async Task CommandFlag_DoesNotConsumeFollowingOption(string command)
    {
        // 回帰: --init 等が FlagArgs に無く、直後の "--log-level" がそのコマンドの値として読まれ、
        // ログレベル (や --Section:Key=値) が効いていなかった
        var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(
            [command, "--log-level", "debug", "--Filler:Enabled=true"]);

        await Assert.That(configuration["Logging:Level"]).IsEqualTo("debug");
        await Assert.That(configuration["Filler:Enabled"]).IsEqualTo("true");
    }

    [Test]
    public async Task WithoutLogOptions_UsesAppSettings()
    {
        var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(["テキスト", "-s", "3", "--no-play"]);

        await Assert.That(configuration["Logging:Level"]).IsEqualTo("info");
    }
}

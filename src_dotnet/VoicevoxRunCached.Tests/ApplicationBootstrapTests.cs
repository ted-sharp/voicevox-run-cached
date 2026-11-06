using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// ApplicationBootstrap.LoadSettings: 設定ファイルの読み込み・バインドの失敗は設定エラー (終了コード 7)。
/// 回帰: これまでは値の検証で引っかかった場合だけ 7 で、JSON の構文エラーや型の不一致は「不明なエラー」(1) だった。
/// </summary>
public class ApplicationBootstrapTests
{
    [Test]
    public async Task ValidJson_LoadsSettings()
    {
        var dir = CreateConfigDirectory("""{ "Audio": { "Volume": 0.5 } }""");
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text"], dir);

            await Assert.That(settings.Audio.Volume).IsEqualTo(0.5);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task MalformedJson_ThrowsConfigurationError()
    {
        var dir = CreateConfigDirectory("""{ "Audio": { "Volume": } }""");
        try
        {
            var ex = await AssertConfigurationErrorAsync(dir);

            // どこが悪いか (JSON パーサの行番号) が利用者に見えること
            await Assert.That(ex.UserMessage).Contains("LineNumber");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task TypeMismatch_ThrowsConfigurationError()
    {
        var dir = CreateConfigDirectory("""{ "Audio": { "Volume": "abc" } }""");
        try
        {
            var ex = await AssertConfigurationErrorAsync(dir);

            await Assert.That(ex.UserMessage).Contains("Audio:Volume");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task MissingAppSettings_ThrowsConfigurationError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_config_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await AssertConfigurationErrorAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task<ConfigurationException> AssertConfigurationErrorAsync(string dir)
    {
        var ex = await Assert.ThrowsAsync<ConfigurationException>(() =>
            Task.FromResult(ApplicationBootstrap.LoadSettings(["text"], dir)));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Configuration.InvalidSettings);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(7);
        return ex;
    }

    private static string CreateConfigDirectory(string appSettingsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_config_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), appSettingsJson);
        return dir;
    }
}

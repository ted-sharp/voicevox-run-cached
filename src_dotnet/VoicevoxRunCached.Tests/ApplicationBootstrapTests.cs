using VoicevoxRunCached.Configuration;
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
    public async Task BaseUrlTrailingSlash_IsRemoved()
    {
        // 末尾の "/" を残すと API の URL が "//version" 等になり、エンジンが動いていても応答が得られない
        var dir = CreateConfigDirectory("""{ "VoiceVox": { "BaseUrl": "http://127.0.0.1:50021/" } }""");
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text"], dir);

            await Assert.That(settings.VoiceVox.BaseUrl).IsEqualTo("http://127.0.0.1:50021");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments("""{ "Lock": { "MaxWaitSeconds": 5 } }""", new string[0], 5)]
    [Arguments("""{ "Lock": { } }""", new string[0], null)]
    [Arguments("""{ }""", new[] { "--Lock:MaxWaitSeconds=3" }, 3)]
    public async Task LockMaxWaitSeconds_IsLoaded(string json, string[] extraArgs, int? expected)
    {
        // Lock セクションは個別に Bind しているので、追加漏れがあると黙って上限なしになる
        var dir = CreateConfigDirectory(json);
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text", .. extraArgs], dir);

            await Assert.That(settings.Lock.MaxWaitSeconds).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SpeakerDevices_IsLoaded()
    {
        var dir = CreateConfigDirectory("""
            { "Audio": { "Devices": ["Headphones (Senary", -1], "DeviceOptions": [{ "Device": "Anker", "LeadingSilenceMs": 800 }], "SpeakerDevices": [
                { "Engine": "Voicevox", "Speaker": "3", "Devices": [1] },
                { "Engine": "voicepeak", "Speaker": "Frimomen", "Devices": ["Jabra", "Anker"] }
            ] } }
            """);
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text"], dir);

            await Assert.That(settings.Audio.SpeakerDevices.Length).IsEqualTo(2);
            await Assert.That(settings.Audio.SpeakerDevices[0].Speaker).IsEqualTo("3");
            // 番号は JSON の数値でも、名前と同じく文字列として読む
            await Assert.That(settings.Audio.SpeakerDevices[0].Devices).IsEquivalentTo(["1"]);
            await Assert.That(settings.Audio.SpeakerDevices[1].Engine).IsEqualTo(SpeechEngineKind.Voicepeak);
            await Assert.That(settings.Audio.SpeakerDevices[1].Devices).IsEquivalentTo(["Jabra", "Anker"]);
            await Assert.That(settings.Audio.Devices).IsEquivalentTo(["Headphones (Senary", "-1"]);
            await Assert.That(settings.Audio.DeviceOptions.Single().Device).IsEqualTo("Anker");
            await Assert.That(settings.Audio.DeviceOptions.Single().LeadingSilenceMs).IsEqualTo(800);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments("""{ "Audio": { "OutputDevice": -1 } }""", new string[0])]
    // コマンドラインの上書きでも同じ
    [Arguments("{}", new[] { "--Audio:OutputDevice=2" })]
    public async Task RemovedOutputDevice_IsConfigurationError(string json, string[] extraArgs)
    {
        // 回帰: 4.0.0 で Audio.Devices に変えた。知らないキーは無視されるので、残っていると書いたデバイスが黙って使われなくなる
        var dir = CreateConfigDirectory(json);
        try
        {
            var ex = await Assert.ThrowsAsync<ConfigurationException>(() =>
                Task.FromResult(ApplicationBootstrap.LoadSettings(["text", .. extraArgs], dir)));

            await Assert.That(ex!.UserMessage).Contains("Audio.Devices");
            await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(7);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments("""{ "Voicepeak": { } }""", 2)]
    [Arguments("""{ "Voicepeak": { "MaxFillersPerWait": null } }""", null)]
    [Arguments("""{ "Voicepeak": { "MaxFillersPerWait": 0 } }""", 0)]
    public async Task MaxFillersPerWait_IsLoaded(string json, int? expected)
    {
        // VOICEPEAK は省略すると 2 (古い appsettings.json でもフィラーが鳴り続けないように)。上限をなくすには null を書く (README の説明)
        var dir = CreateConfigDirectory(json);
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text"], dir);

            await Assert.That(settings.Voicepeak.MaxFillersPerWait).IsEqualTo(expected);
            await Assert.That(settings.VoiceVox.MaxFillersPerWait).IsNull();
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

    [Test]
    public async Task Cevio_IsLoaded()
    {
        // Cevio セクションは個別に Bind しているので、追加漏れがあると黙って既定値になる
        var dir = CreateConfigDirectory("""{ "Cevio": { "DefaultSpeaker": "花隈千冬", "KeepHostRunning": false, "StartupTimeoutSeconds": 90 } }""");
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text", "--Cevio:AutoStartHost=false"], dir);

            await Assert.That(settings.Cevio.DefaultSpeaker).IsEqualTo("花隈千冬");
            await Assert.That(settings.Cevio.KeepHostRunning).IsFalse();
            await Assert.That(settings.Cevio.StartupTimeoutSeconds).IsEqualTo(90);
            await Assert.That(settings.Cevio.AutoStartHost).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CevioSpeakerWithEmotions_IsNotTakenAsConfiguration()
    {
        // "花隈千冬:嬉しい=50" は設定の読み込みでは "キー=値" の形に見える。設定のセクションに当たらず、読み込みが失敗しないこと
        var dir = CreateConfigDirectory("""{ "Cevio": { "DefaultSpeaker": "花隈千冬" } }""");
        try
        {
            var settings = ApplicationBootstrap.LoadSettings(["text", "--engine", "cevio", "-s", "花隈千冬:嬉しい=50,普通=50"], dir);

            await Assert.That(settings.Cevio.DefaultSpeaker).IsEqualTo("花隈千冬");
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

using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class OutputDeviceSelectorTests
{
    private static AudioSettings Settings() => new()
    {
        OutputDevice = "5",
        SpeakerDevices =
        [
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "3", Device = "1" },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicepeak, Speaker = "Frimomen:happy=50", Device = "Jabra" },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Cevio, Speaker = "さとうささら", Device = "-1" },
        ],
    };

    // この PC の WaveOut の一覧の例 (名前は 31 文字に切られている)
    private static readonly string[] Names =
    [
        "Headphones (Senary Audio)",
        "1 - DELL S3422DWG (AMD High Def",
        "スピーカー (Senary Audio)",
        "スピーカー (Jabra SPEAK 510 USB)",
    ];

    [Test]
    [Arguments(SpeechEngineKind.Voicevox, "3", "1")]
    [Arguments(SpeechEngineKind.Voicevox, "2", "5")]
    // VOICEPEAK・CeVIO は感情を見ずにキャラクターで照合する
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen", "Jabra")]
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen:angry=10", "Jabra")]
    [Arguments(SpeechEngineKind.Cevio, "さとうささら:元気=100", "-1")]
    // 同じ話者の文字列でもエンジンが違えば当てない
    [Arguments(SpeechEngineKind.Cevio, "3", "5")]
    public async Task Resolve_BySpeaker(SpeechEngineKind engine, string speaker, string expected)
    {
        var request = new VoiceRequest(speaker: speaker, engine: engine);

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: null)).IsEqualTo(expected);
    }

    [Test]
    public async Task Resolve_DeviceOptionWins()
    {
        var request = new VoiceRequest(speaker: "3");

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: "headphones")).IsEqualTo("headphones");
    }

    [Test]
    public async Task Resolve_SpeakerWrittenDifferently_Matches()
    {
        // 設定の "03" も正規化して照合する (--speaker 03 と同じ話者)
        var settings = new AudioSettings
        {
            SpeakerDevices = [new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "03", Device = "4" }],
        };

        await Assert.That(OutputDeviceSelector.Resolve(settings, new VoiceRequest(speaker: "3"), null)).IsEqualTo("4");
    }

    [Test]
    [Arguments("-1", true)]
    [Arguments("0", true)]
    [Arguments("Jabra", true)]
    [Arguments("-2", false)]
    [Arguments("", false)]
    [Arguments("  ", false)]
    public async Task TryValidateSpec(string spec, bool expected)
    {
        await Assert.That(OutputDeviceSelector.TryValidateSpec(spec, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("-1", -1)]
    [Arguments("3", 3)]
    // 名前の一部で指定できる (大文字小文字は区別しない)。抜き差しで番号がずれても同じデバイスを選べるように
    [Arguments("jabra", 3)]
    [Arguments("DELL", 1)]
    [Arguments("headphones", 0)]
    // 名前全体が一致すれば、部分一致の候補が複数あってもそれを選ぶ
    [Arguments("スピーカー (Senary Audio)", 2)]
    public async Task ToIndex_FindsDevice(string spec, int expected)
    {
        var choice = OutputDeviceSelector.ToIndex(spec, Names, fallbackToDefault: false);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(expected, FellBackToDefault: false));
    }

    [Test]
    public async Task ToIndex_OsDefault_ExistsWithoutDevices()
    {
        await Assert.That(OutputDeviceSelector.ToIndex("-1", [], fallbackToDefault: false).Index).IsEqualTo(-1);
    }

    [Test]
    [Arguments("4")]
    [Arguments("nothing")]
    public async Task ToIndex_Missing_FailsWithAudioExitCode(string spec)
    {
        // 回帰: 抜き差しで番号がずれると、無い番号が設定に残る。確かめないと合成の後に waveOutOpen で
        // スタックトレース付きの終了コード 1 になっていた
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.ToIndex(spec, Names, fallbackToDefault: false);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceNotFound);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(5);
        // つながっているデバイスを案内する
        await Assert.That(ex.UserMessage).Contains("3: \"スピーカー (Jabra SPEAK 510 USB)\"");
    }

    [Test]
    [Arguments("4")]
    [Arguments("nothing")]
    public async Task ToIndex_MissingWithFallback_UsesOsDefault(string spec)
    {
        var choice = OutputDeviceSelector.ToIndex(spec, Names, fallbackToDefault: true);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(-1, FellBackToDefault: true));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ToIndex_Ambiguous_FailsEvenWithFallback(bool fallbackToDefault)
    {
        // どれを鳴らすか決められないのは書き方の問題なので、OS 既定に逃がさずエラーにする
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.ToIndex("senary", Names, fallbackToDefault);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceAmbiguous);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(5);
        await Assert.That(ex.UserMessage).Contains("0: \"Headphones (Senary Audio)\", 2: \"スピーカー (Senary Audio)\"");
    }
}

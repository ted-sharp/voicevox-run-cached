using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class OutputDeviceSelectorTests
{
    private static AudioSettings Settings() => new()
    {
        OutputDevice = 5,
        SpeakerDevices =
        [
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "3", Device = 1 },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicepeak, Speaker = "Frimomen:happy=50", Device = 2 },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Cevio, Speaker = "さとうささら", Device = -1 },
        ],
    };

    [Test]
    [Arguments(SpeechEngineKind.Voicevox, "3", 1)]
    [Arguments(SpeechEngineKind.Voicevox, "2", 5)]
    // VOICEPEAK・CeVIO は感情を見ずにキャラクターで照合する
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen", 2)]
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen:angry=10", 2)]
    [Arguments(SpeechEngineKind.Cevio, "さとうささら:元気=100", -1)]
    // 同じ話者の文字列でもエンジンが違えば当てない
    [Arguments(SpeechEngineKind.Cevio, "3", 5)]
    public async Task Resolve_BySpeaker(SpeechEngineKind engine, string speaker, int expected)
    {
        var request = new VoiceRequest(speaker: speaker, engine: engine);

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: null)).IsEqualTo(expected);
    }

    [Test]
    public async Task Resolve_DeviceOptionWins()
    {
        var request = new VoiceRequest(speaker: "3");

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: 0)).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_SpeakerWrittenDifferently_Matches()
    {
        // 設定の "03" も正規化して照合する (--speaker 03 と同じ話者)
        var settings = new AudioSettings
        {
            SpeakerDevices = [new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "03", Device = 4 }],
        };

        await Assert.That(OutputDeviceSelector.Resolve(settings, new VoiceRequest(speaker: "3"), null)).IsEqualTo(4);
    }

    [Test]
    [Arguments(-1, 0)]
    [Arguments(0, 1)]
    [Arguments(2, 3)]
    public async Task EnsureExists_ExistingDevice_DoesNotThrow(int device, int deviceCount)
    {
        OutputDeviceSelector.EnsureExists(device, deviceCount);

        await Assert.That(device).IsLessThan(deviceCount);
    }

    [Test]
    public async Task EnsureExists_MissingDevice_FailsWithAudioExitCode()
    {
        // 回帰: 抜き差しで番号がずれると、無い番号が設定に残る。確かめないと合成の後に waveOutOpen で
        // スタックトレース付きの終了コード 1 になっていた
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.EnsureExists(3, 3);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceNotFound);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(5);
        await Assert.That(ex.UserMessage).Contains("0〜2");
    }
}

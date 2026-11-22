using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class OutputDeviceSelectorTests
{
    private static AudioSettings Settings() => new()
    {
        Devices = ["Jabra", "-1"],
        SpeakerDevices =
        [
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "3", Devices = ["1"] },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicepeak, Speaker = "Frimomen:happy=50", Devices = ["Anker", "Headphones (Senary"] },
            new SpeakerDeviceSettings { Engine = SpeechEngineKind.Cevio, Speaker = "さとうささら", Devices = ["-1"] },
        ],
    };

    // この PC の WaveOut の一覧の例 (名前は 31 文字に切られている)
    private static readonly string[] Names =
    [
        "Headphones (Senary Audio)",
        "Headphones (Anker SoundCore)",
        "スピーカー (Senary Audio)",
        "スピーカー (Jabra SPEAK 510 USB)",
        "1 - DELL S3422DWG (AMD High Def",
    ];

    // OS 既定の名前 (Core Audio の名前は切られていない)
    private const string DellOsDefault = "1 - DELL S3422DWG (AMD High Definition Audio Device)";

    [Test]
    [Arguments(SpeechEngineKind.Voicevox, "3", new[] { "1", "Jabra", "-1" })]
    // 対応表に無い話者は Audio.Devices だけ
    [Arguments(SpeechEngineKind.Voicevox, "2", new[] { "Jabra", "-1" })]
    // VOICEPEAK・CeVIO は感情を見ずにキャラクターで照合する。話者の候補の後に Audio.Devices を続ける
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen", new[] { "Anker", "Headphones (Senary", "Jabra", "-1" })]
    [Arguments(SpeechEngineKind.Voicepeak, "Frimomen:angry=10", new[] { "Anker", "Headphones (Senary", "Jabra", "-1" })]
    [Arguments(SpeechEngineKind.Cevio, "さとうささら:元気=100", new[] { "-1", "Jabra", "-1" })]
    // 同じ話者の文字列でもエンジンが違えば当てない
    [Arguments(SpeechEngineKind.Cevio, "3", new[] { "Jabra", "-1" })]
    public async Task Resolve_BySpeaker(SpeechEngineKind engine, string speaker, string[] expected)
    {
        var request = new VoiceRequest(speaker: speaker, engine: engine);

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: null)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Resolve_DeviceOption_ReplacesSpeakerCandidates()
    {
        var request = new VoiceRequest(speaker: "3");

        await Assert.That(OutputDeviceSelector.Resolve(Settings(), request, deviceOverride: "anker")).IsEquivalentTo(["anker", "Jabra", "-1"]);
    }

    [Test]
    public async Task Resolve_NoDevices_IsOsDefault()
    {
        // 何も書いていなければ OS 既定
        await Assert.That(OutputDeviceSelector.Resolve(new AudioSettings(), new VoiceRequest(speaker: "3"), null)).IsEquivalentTo(["-1"]);
    }

    [Test]
    public async Task Resolve_SpeakerWrittenDifferently_Matches()
    {
        // 設定の "03" も正規化して照合する (--speaker 03 と同じ話者)
        var settings = new AudioSettings
        {
            SpeakerDevices = [new SpeakerDeviceSettings { Engine = SpeechEngineKind.Voicevox, Speaker = "03", Devices = ["4"] }],
        };

        await Assert.That(OutputDeviceSelector.Resolve(settings, new VoiceRequest(speaker: "3"), null)).IsEquivalentTo(["4", "-1"]);
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
    [Arguments("anker", 1)]
    [Arguments("headphones (senary", 0)]
    // 名前全体が一致すれば、部分一致の候補が複数あってもそれを選ぶ
    [Arguments("スピーカー (Senary Audio)", 2)]
    public async Task ToIndex_FindsDevice(string spec, int expected)
    {
        var choice = OutputDeviceSelector.ToIndex([spec], Names, [], null);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(expected, spec, IsFirstCandidate: true));
    }

    [Test]
    public async Task ToIndex_OsDefault_ExistsWithoutDevices()
    {
        await Assert.That(OutputDeviceSelector.ToIndex(["-1"], [], [], null).Index).IsEqualTo(-1);
    }

    [Test]
    public async Task ToIndex_TriesCandidatesInOrder()
    {
        // 見つからない候補は飛ばし、上から探して最初に見つかったものを使う
        var choice = OutputDeviceSelector.ToIndex(["nothing", "9", "anker", "jabra"], Names, [], null);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(1, "anker", IsFirstCandidate: false));
    }

    [Test]
    public async Task ToIndex_OsDefaultAsLastCandidate_IsUsedWhenNothingElseFound()
    {
        // -1 は常にあるので、最後に書けばどれも無いとき OS 既定で鳴る
        var choice = OutputDeviceSelector.ToIndex(["nothing", "usb headset", "-1"], Names, [], null);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(-1, "-1", IsFirstCandidate: false));
    }

    [Test]
    [Arguments("9")]
    [Arguments("nothing")]
    public async Task ToIndex_NothingFound_FailsWithAudioExitCode(string spec)
    {
        // 回帰: 抜き差しで番号がずれると、無い番号が設定に残る。確かめないと合成の後に waveOutOpen で
        // スタックトレース付きの終了コード 1 になっていた
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.ToIndex([spec, "usb headset"], Names, [], null);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceNotFound);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(5);
        // 探した候補と、つながっているデバイスを案内する
        await Assert.That(ex.UserMessage).Contains("\"usb headset\") が見つかりません");
        await Assert.That(ex.UserMessage).Contains("3: \"スピーカー (Jabra SPEAK 510 USB)\"");
    }

    [Test]
    [Arguments("headphones|-1")]
    // 後ろの候補で複数に合っても、次の候補に進まずエラーにする
    [Arguments("nothing|senary|-1")]
    public async Task ToIndex_Ambiguous_Fails(string candidateList)
    {
        // どれを鳴らすか決められないのは書き方の問題なので、ほかのデバイスに逃がさずエラーにする
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.ToIndex(candidateList.Split('|'), Names, [], null);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceAmbiguous);
        await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(5);
        await Assert.That(ex.UserMessage).Contains("に合う再生デバイスが複数あります: 0: \"Headphones (Senary Audio)\"");
    }

    [Test]
    [Arguments("4")]
    [Arguments("dell")]
    public async Task ToIndex_ExcludedDevice_IsSkipped(string spec)
    {
        // つながっていても鳴らないデバイス (モニターなど) は、番号でも名前でも、つながっていないものとして次の候補に進む
        var choice = OutputDeviceSelector.ToIndex([spec, "jabra"], Names, ["DELL"], null);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(3, "jabra", IsFirstCandidate: false));
    }

    [Test]
    public async Task ToIndex_OsDefaultExcluded_IsSkipped()
    {
        // OS がモニターを既定に切り替えると、-1 で鳴らしても聞こえない
        var choice = OutputDeviceSelector.ToIndex(["-1", "jabra"], Names, ["DELL"], DellOsDefault);

        await Assert.That(choice).IsEqualTo(new DeviceChoice(3, "jabra", IsFirstCandidate: false));
    }

    [Test]
    public async Task ToIndex_OsDefaultExcludedAsLastCandidate_Fails()
    {
        // 聞こえないデバイスで鳴らすより、エラーで気づけるようにする
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            OutputDeviceSelector.ToIndex(["nothing", "-1"], Names, ["DELL"], DellOsDefault);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.OutputDeviceNotFound);
        await Assert.That(ex.UserMessage).Contains("除外: -1 (OS 既定 = \"" + DellOsDefault + "\"), 4: \"1 - DELL S3422DWG (AMD High Def\"");
        await Assert.That(ex.UserMessage).DoesNotContain("使えるのは -1");
    }

    [Test]
    public async Task ToIndex_OsDefaultNameUnknown_UsesOsDefault()
    {
        // OS 既定の名前が取れなければ、除外かどうか分からないので -1 を使う
        var choice = OutputDeviceSelector.ToIndex(["-1"], Names, ["DELL"], null);

        await Assert.That(choice.Index).IsEqualTo(-1);
    }

    [Test]
    public async Task ToIndex_ExcludedDevicesAreNotCountedAsMatches()
    {
        // "senary" は 0 と 2 に合うが、2 を除外すれば 1 台に絞れる
        var choice = OutputDeviceSelector.ToIndex(["senary"], Names, ["スピーカー (senary"], null);

        await Assert.That(choice.Index).IsEqualTo(0);
    }

    [Test]
    [Arguments("DELL", true)]
    [Arguments("2", false)]
    [Arguments("-1", false)]
    [Arguments("", false)]
    public async Task TryValidateNameSpec(string spec, bool expected)
    {
        // 番号は抜き差しでずれて別のデバイスを除外してしまうので、名前だけ
        await Assert.That(OutputDeviceSelector.TryValidateNameSpec(spec, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Headphones (Anker SoundCore)", 800)]
    // 最初に合う項目を使う
    [Arguments("スピーカー (Senary Audio)", 300)]
    [Arguments("スピーカー (Jabra SPEAK 510 USB)", 100)]
    [Arguments("1 - DELL S3422DWG (AMD High Def", null)]
    // OS 既定の名前が取れなかった (-1 で鳴らす) ときは何もしない
    [Arguments(null, null)]
    public async Task FindOptions_MatchesDeviceName(string? deviceName, int? expectedSilenceMs)
    {
        DeviceOptionSettings[] options =
        [
            new() { Device = "anker", LeadingSilenceMs = 800 },
            new() { Device = "Senary", LeadingSilenceMs = 300 },
            new() { Device = "スピーカー", LeadingSilenceMs = 100 },
        ];

        await Assert.That(OutputDeviceSelector.FindOptions(deviceName, options)?.LeadingSilenceMs).IsEqualTo(expectedSilenceMs);
    }
}

using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// CeVIO AI のエンジン (COM は <see cref="FakeCevioRemoteService"/> で置き換える)。
/// </summary>
public class CevioEngineTests
{
    private const string Cast = FakeCevioRemoteService.Cast;

    // 話者の正規化 -----------------------------------------------------------

    [Test]
    [Arguments("花隈千冬", "花隈千冬")]
    [Arguments("  花隈千冬 ", "花隈千冬")]
    [Arguments("花隈千冬:嬉しい=50", "花隈千冬:嬉しい=50")]
    [Arguments("花隈千冬:普通=50,嬉しい=50", "花隈千冬:嬉しい=50,普通=50")]
    [Arguments("花隈千冬: 普通 = 050 , 嬉しい=0", "花隈千冬:嬉しい=0,普通=50")]
    public async Task NormalizeSpeaker_SameSpeakerIsSameString(string raw, string expected)
    {
        // キャッシュキーに入るので、感情の順番・空白・0 埋めが違っても同じ文字列にする
        await Assert.That(CevioEngine.NormalizeSpeaker(raw, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(":嬉しい=50")]
    [Arguments("花隈千冬:")]
    [Arguments("花隈千冬:嬉しい")]
    [Arguments("花隈千冬:=50")]
    [Arguments("花隈千冬:嬉しい=101")]
    [Arguments("花隈千冬:嬉しい=-1")]
    [Arguments("花隈千冬:嬉しい=5.5")]
    [Arguments("花隈千冬:嬉しい=50,")]
    [Arguments("花隈千冬:嬉しい=50,嬉しい=10")]
    public async Task NormalizeSpeaker_Invalid_ReturnsError(string raw)
    {
        var normalized = CevioEngine.NormalizeSpeaker(raw, out var error);

        await Assert.That(normalized).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task CacheKey_IncludesEngineAndEmotions()
    {
        var plain = new VoiceRequest { Text = "テスト", Engine = SpeechEngineKind.Cevio, Speaker = Cast };
        var happy = new VoiceRequest { Text = "テスト", Engine = SpeechEngineKind.Cevio, Speaker = $"{Cast}:嬉しい=100" };

        await Assert.That(CacheKeyGenerator.SpeakerKey(happy)).IsEqualTo($"cevio:{Cast}:嬉しい=100");
        await Assert.That(CacheKeyGenerator.Compute(happy)).IsNotEqualTo(CacheKeyGenerator.Compute(plain));
    }

    // パラメータの換算 -------------------------------------------------------

    [Test]
    [Arguments(1.0, 50u)]
    [Arguments(2.0, 75u)]
    [Arguments(0.5, 25u)]
    [Arguments(4.0, 100u)]
    [Arguments(10.0, 100u)]
    [Arguments(0.1, 0u)]
    [Arguments(1.004, 50u)]
    public async Task ToCevioSpeed_IsLogarithmic(double speed, uint expected)
    {
        // CeVIO の Speed は 25 ごとに約 2 倍 (実測)。範囲外は端に丸める
        await Assert.That(CevioEngine.ToCevioSpeed(speed)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0.0, 50u)]
    [Arguments(0.15, 100u)]
    [Arguments(-0.15, 0u)]
    [Arguments(0.03, 60u)]
    [Arguments(1.0, 100u)]
    [Arguments(-1.0, 0u)]
    public async Task ToCevioTone_MapsPitchRangeToScale(double pitch, uint expected)
    {
        await Assert.That(CevioEngine.ToCevioTone(pitch)).IsEqualTo(expected);
    }

    // 準備 -------------------------------------------------------------------

    [Test]
    public async Task Prepare_UnknownCast_ThrowsInvalidArgumentsWithoutSelectingIt()
    {
        // 存在しないキャストを Cast に代入すると CeVIO AI がダイアログを出すので、COM に渡す前に弾く
        var remote = new FakeCevioRemoteService();
        var engine = TestEngines.Cevio(remote);

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.PrepareAsync("存在しないキャスト", CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
        await Assert.That(remote.ComponentRequests).IsEmpty();
    }

    [Test]
    public async Task Prepare_UnknownEmotion_ThrowsInvalidArguments()
    {
        var engine = TestEngines.Cevio(new FakeCevioRemoteService());

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.PrepareAsync($"{Cast}:哀しみ=50", CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    public async Task Prepare_HostNotStartedAndNoAutoStart_ThrowsEngineNotAvailable()
    {
        var remote = new FakeCevioRemoteService { HostStarted = () => false };
        var engine = TestEngines.Cevio(remote, new CevioSettings { AutoStartHost = false });

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.PrepareAsync(Cast, CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(engine.IsEngineUnavailable(ex)).IsTrue();
    }

    // 合成 -------------------------------------------------------------------

    [Test]
    public async Task Synthesize_WithoutEmotions_UsesCastDefaults()
    {
        var remote = new FakeCevioRemoteService();
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync(Cast, CancellationToken.None);

        await engine.SynthesizeAsync(Request(Cast), CancellationToken.None);

        await Assert.That(remote.Talks[0].Components["普通"]).IsEqualTo(100u);
        await Assert.That(remote.Talks[0].Components["嬉しい"]).IsEqualTo(0u);
    }

    [Test]
    public async Task Synthesize_WithEmotions_SetsUnspecifiedEmotionsToZero()
    {
        // Talker は使い回すので、書いていない感情も毎回すべて設定する (前の合成の値・キャストの既定値を残さない)
        var remote = new FakeCevioRemoteService();
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync($"{Cast}:嬉しい=70", CancellationToken.None);

        await engine.SynthesizeAsync(Request($"{Cast}:嬉しい=70"), CancellationToken.None);

        await Assert.That(remote.Talks[0].Components.Count).IsEqualTo(3);
        await Assert.That(remote.Talks[0].Components["嬉しい"]).IsEqualTo(70u);
        await Assert.That(remote.Talks[0].Components["普通"]).IsEqualTo(0u);
        await Assert.That(remote.Talks[0].Components["怒り"]).IsEqualTo(0u);
    }

    [Test]
    public async Task Synthesize_ConvertsSpeedAndPitch()
    {
        var remote = new FakeCevioRemoteService();
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync(Cast, CancellationToken.None);

        await engine.SynthesizeAsync(Request(Cast, speed: 2.0, pitch: -0.15), CancellationToken.None);

        await Assert.That(remote.Talks[0].Cast).IsEqualTo(Cast);
        await Assert.That(remote.Talks[0].Speed).IsEqualTo(75u);
        await Assert.That(remote.Talks[0].Tone).IsEqualTo(0u);
    }

    [Test]
    [Arguments(1.0, 10000)]
    [Arguments(0.5, 5000)]
    [Arguments(0.0, 0)]
    [Arguments(5.0, 32767)]
    public async Task Synthesize_VolumeIsAppliedToWaveform(double volume, int expectedSample)
    {
        // --volume は VOICEVOX の volumeScale と同じく振幅の倍率。割れる値は端で止める
        var remote = new FakeCevioRemoteService { SampleValue = 10000 };
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync(Cast, CancellationToken.None);

        var wav = await engine.SynthesizeAsync(Request(Cast, volume: volume), CancellationToken.None);

        using var reader = new WaveFileReader(new MemoryStream(wav));
        var buffer = new byte[2];
        await reader.ReadExactlyAsync(buffer);
        await Assert.That((int)BitConverter.ToInt16(buffer)).IsBetween(expectedSample - 1, expectedSample);
        await Assert.That(reader.WaveFormat.SampleRate).IsEqualTo(48000);
    }

    [Test]
    public async Task Synthesize_OutputFailsWhileHostRuns_IsSegmentFailure()
    {
        var remote = new FakeCevioRemoteService { OutputResult = false };
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync(Cast, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.SynthesizeAsync(Request(Cast), CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.AudioGenerationFailed);
        await Assert.That(engine.IsEngineUnavailable(ex)).IsFalse();
    }

    [Test]
    public async Task Synthesize_OutputFailsAfterHostClosed_IsEngineUnavailable()
    {
        // CeVIO AI が閉じられたら、残りの文を 1 件ずつ試さずに止める
        var remote = new FakeCevioRemoteService();
        var engine = TestEngines.Cevio(remote);
        await engine.PrepareAsync(Cast, CancellationToken.None);
        remote.OutputResult = false;
        remote.HostStarted = () => false;

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.SynthesizeAsync(Request(Cast), CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(engine.IsEngineUnavailable(ex)).IsTrue();
    }

    [Test]
    public async Task GetSpeakers_ListsCastAndEmotionsAsSpeakerValues()
    {
        var engine = TestEngines.Cevio(new FakeCevioRemoteService());

        var speakers = await engine.GetSpeakersAsync(CancellationToken.None);

        await Assert.That(speakers.Count).IsEqualTo(1);
        await Assert.That(speakers[0].Name).IsEqualTo(Cast);
        var ids = speakers[0].Voices.Select(v => v.Id).ToList();
        await Assert.That(ids).IsEquivalentTo([Cast, $"{Cast}:嬉しい=100", $"{Cast}:普通=100", $"{Cast}:怒り=100"]);
        // 一覧の ID はそのまま -s に渡せる (正規化しても変わらない)
        foreach (var id in ids)
        {
            await Assert.That(CevioEngine.NormalizeSpeaker(id, out _)).IsEqualTo(id);
        }
    }

    private static VoiceRequest Request(string speaker, double speed = 1.0, double pitch = 0.0, double volume = 1.0) => new()
    {
        Text = "テストです。",
        Engine = SpeechEngineKind.Cevio,
        Speaker = speaker,
        Speed = speed,
        Pitch = pitch,
        Volume = volume,
    };
}

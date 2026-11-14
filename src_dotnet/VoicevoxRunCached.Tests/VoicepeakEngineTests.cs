using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VOICEPEAK (voicepeak.exe) のエンジン。exe を実際に呼ぶ合成は実機で確認する (1 回 10 秒ほどかかるため)。
/// </summary>
public class VoicepeakEngineTests
{
    [Test]
    [Arguments("Otomachi Una", "Otomachi Una")]
    [Arguments("  Otomachi Una  ", "Otomachi Una")]
    [Arguments("Otomachi Una:joy=50", "Otomachi Una:joy=50")]
    [Arguments("Otomachi Una: sad = 20 , joy=050", "Otomachi Una:joy=50,sad=20")]
    [Arguments("Otomachi Una:joy=0", "Otomachi Una")]
    [Arguments("Otomachi Una:joy=0,sad=100", "Otomachi Una:sad=100")]
    public async Task NormalizeSpeaker_ReturnsCanonicalForm(string raw, string expected)
    {
        // キャッシュキーに入るので、同じ指定 (感情の順序・値の書き方・値 0) は同じ文字列にする
        var normalized = VoicepeakEngine.NormalizeSpeaker(raw, out var error);

        await Assert.That(normalized).IsEqualTo(expected);
        await Assert.That(error).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(":joy=50")]
    [Arguments("Otomachi Una:")]
    [Arguments("Otomachi Una:joy")]
    [Arguments("Otomachi Una:joy=abc")]
    [Arguments("Otomachi Una:joy=101")]
    [Arguments("Otomachi Una:joy=-1")]
    [Arguments("Otomachi Una:joy=1,joy=2")]
    [Arguments("Otomachi Una:=50")]
    public async Task NormalizeSpeaker_Invalid_ReturnsError(string raw)
    {
        var normalized = VoicepeakEngine.NormalizeSpeaker(raw, out var error);

        await Assert.That(normalized).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    [Arguments(1.0, 100)]
    [Arguments(0.5, 50)]
    [Arguments(2.0, 200)]
    [Arguments(1.234, 123)]
    public async Task ToSpeed_ConvertsToVoicepeakScale(double speed, int expected)
    {
        await Assert.That(VoicepeakEngine.ToSpeed(speed)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0.0, 0)]
    [Arguments(0.15, 300)]
    [Arguments(-0.15, -300)]
    [Arguments(0.05, 100)]
    public async Task ToPitch_ConvertsToVoicepeakScale(double pitch, int expected)
    {
        await Assert.That(VoicepeakEngine.ToPitch(pitch)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0.5, 0.15)]
    [Arguments(2.0, -0.15)]
    [Arguments(1.0, 0.0)]
    public async Task ValidateParameters_InRange_ReturnsNull(double speed, double pitch)
    {
        await Assert.That(VoicepeakEngine.ValidateParameters(new VoiceRequest("文", "Otomachi Una", speed, pitch))).IsNull();
    }

    [Test]
    [Arguments(0.49, 0.0, "--speed")]
    [Arguments(2.01, 0.0, "--speed")]
    [Arguments(1.0, 0.16, "--pitch")]
    [Arguments(1.0, -0.16, "--pitch")]
    public async Task ValidateParameters_OutOfRange_ReturnsOption(double speed, double pitch, string option)
    {
        var invalid = VoicepeakEngine.ValidateParameters(new VoiceRequest("文", "Otomachi Una", speed, pitch));

        await Assert.That(invalid?.Option).IsEqualTo(option);
    }

    [Test]
    public async Task BuildSynthesisArguments_PassesValuesWithEquals()
    {
        // 値を --name=値 で渡す: - で始まる本文や負の音高をオプションと取り違えさせない
        var args = VoicepeakEngine.BuildSynthesisArguments(
            new VoiceRequest("-ハイフンで始まる文", "Otomachi Una", speed: 1.5, pitch: -0.1), @"C:\tmp\out.wav");

        await Assert.That(String.Join("|", args)).IsEqualTo(
            @"--say=-ハイフンで始まる文|--narrator=Otomachi Una|--out=C:\tmp\out.wav|--speed=150|--pitch=-200");
    }

    [Test]
    public async Task BuildSynthesisArguments_WithEmotion_PassesEmotion()
    {
        var args = VoicepeakEngine.BuildSynthesisArguments(new VoiceRequest("文", "Otomachi Una:joy=50,sad=20"), "out.wav");

        await Assert.That(args).Contains("--narrator=Otomachi Una");
        await Assert.That(args).Contains("--emotion=joy=50,sad=20");
    }

    [Test]
    public async Task ParseLines_SkipsBlankLines()
    {
        var lines = VoicepeakEngine.ParseLines("Otomachi Una\r\n\r\n  Japanese Female 1  \r\n");

        await Assert.That(String.Join("|", lines)).IsEqualTo("Otomachi Una|Japanese Female 1");
    }

    [Test]
    public async Task CacheKey_IncludesEngineAndEmotion()
    {
        // VOICEPEAK の話者はエンジン名付きでキーに入る。感情が違えば別のキー
        var plain = new VoiceRequest("文", "Otomachi Una", engine: SpeechEngineKind.Voicepeak);
        var joy = new VoiceRequest("文", "Otomachi Una:joy=50", engine: SpeechEngineKind.Voicepeak);

        await Assert.That(CacheKeyGenerator.SpeakerKey(joy)).IsEqualTo("voicepeak:Otomachi Una:joy=50");
        await Assert.That(CacheKeyGenerator.Compute(plain)).IsNotEqualTo(CacheKeyGenerator.Compute(joy));
        await Assert.That(CacheKeyGenerator.Compute(plain))
            .IsNotEqualTo(CacheKeyGenerator.Compute(new VoiceRequest("文", "Otomachi Una")));
    }

    [Test]
    public async Task MissingExe_IsEngineNotAvailable()
    {
        var engine = CreateEngine(Path.Combine(Path.GetTempPath(), $"vrc_missing_{Guid.NewGuid():N}", "voicepeak.exe"));

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => engine.PrepareAsync("Otomachi Una", CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(engine.IsEngineUnavailable(ex)).IsTrue();
    }

    [Test]
    public async Task NoOutputFile_FailsAndStopsRemainingSynthesis()
    {
        // voicepeak.exe の代わりに cmd.exe を呼ぶ (引数を無視し、閉じた標準入力を読んで WAV を出さずに終わる)。
        // 存在しない話者と同じく WAV が無いので失敗にし、残りの文は合成しない (IsEngineUnavailable)
        var engine = CreateEngine(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            engine.SynthesizeAsync(new VoiceRequest("文", "Nobody"), CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineProcessError);
        await Assert.That(engine.IsEngineUnavailable(ex)).IsTrue();
    }

    [Test]
    public async Task ApplyGain_ReadsWavWithJunkChunk()
    {
        // voicepeak.exe の WAV は fmt の前に JUNK チャンクがある
        var wav = InsertJunkChunk(TestAudio.SineWav(48000, 300));

        var half = AudioConversionUtility.ApplyGain(wav, 0.5f);

        using var reader = new WaveFileReader(new MemoryStream(half));
        await Assert.That(reader.WaveFormat.SampleRate).IsEqualTo(48000);
        await Assert.That(TestAudio.Rms(half)).IsEqualTo(TestAudio.Rms(wav) / 2).Within(0.01);
    }

    [Test]
    public async Task Parse_Voicepeak_UsesNormalizedDefaultSpeaker()
    {
        var settings = new AppSettings { Voicepeak = new VoicepeakSettings { DefaultSpeaker = "Otomachi Una:sad=0,joy=50" } };

        var (request, _) = ArgumentParser.Parse(["文", "--engine", "voicepeak"], settings);

        await Assert.That(request!.Engine).IsEqualTo(SpeechEngineKind.Voicepeak);
        await Assert.That(request.Speaker).IsEqualTo("Otomachi Una:joy=50");
    }

    [Test]
    [Arguments("--engine voicepeak")]
    [Arguments("--engine voicepeak -s Otomachi_Una:joy=abc")]
    [Arguments("--engine voicepeak -s Otomachi_Una --speed 3")]
    [Arguments("--engine voicepeak -s Otomachi_Una --pitch 0.2")]
    public async Task Parse_Voicepeak_Invalid_ThrowsInvalidArguments(string options)
    {
        // 既定の話者が未設定なのに -s が無い・感情の書式・範囲外の話速/音高は、ロックを待つ前にエラーにする
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["文", .. options.Split(' ')], new AppSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    public async Task Parse_InvalidDefaultSpeaker_ThrowsConfigurationError()
    {
        // 設定の検証を通っていれば起きないが、正規化できない既定の話者を黙ってそのまま使わない
        var settings = new AppSettings { Voicepeak = new VoicepeakSettings { DefaultSpeaker = "Otomachi Una:joy=abc" } };

        var ex = await Assert.ThrowsAsync<ConfigurationException>(() =>
            Task.FromResult(ArgumentParser.Parse(["文", "--engine", "voicepeak"], settings)));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Configuration.InvalidSettings);
    }

    [Test]
    public async Task Parse_Voicevox_AllowsSpeedOutsideVoicepeakRange()
    {
        // VOICEPEAK の範囲は VOICEPEAK のときだけ
        var (request, _) = ArgumentParser.Parse(["文", "--speed", "3"], new AppSettings());

        await Assert.That(request!.Speed).IsEqualTo(3.0);
    }

    [Test]
    [Arguments("Otomachi Una:joy=abc", 60)]
    [Arguments("", 5)]
    public async Task SettingsValidator_RejectsInvalidValues(string defaultSpeaker, int timeoutSeconds)
    {
        var result = new VoicepeakSettingsValidator().Validate(
            new VoicepeakSettings { DefaultSpeaker = defaultSpeaker, TimeoutSeconds = timeoutSeconds });

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task SettingsValidator_Default_IsValid()
    {
        await Assert.That(new VoicepeakSettingsValidator().Validate(new VoicepeakSettings()).IsValid).IsTrue();
    }

    private static VoicepeakEngine CreateEngine(string exePath)
        => new(new VoicepeakSettings { ExePath = exePath, TimeoutSeconds = 30 }, NullLogger.Instance);

    /// <summary>
    /// RIFF ヘッダの直後 (fmt の前) に JUNK チャンクを入れる。
    /// </summary>
    private static byte[] InsertJunkChunk(byte[] wav)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(wav, 0, 12); // RIFF <size> WAVE
        writer.Write(Encoding.ASCII.GetBytes("JUNK"));
        writer.Write(28);
        writer.Write(new byte[28]);
        writer.Write(wav, 12, wav.Length - 12);
        writer.Flush();

        var result = output.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        return result;
    }
}

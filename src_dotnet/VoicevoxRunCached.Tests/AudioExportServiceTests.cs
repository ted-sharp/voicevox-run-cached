using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// キャッシュヒット (Opus, デコード後 48kHz) と新規合成 (WAV, VOICEVOX は 24kHz) が混在した状態での --out 出力を検証する。
/// </summary>
public class AudioExportServiceTests
{
    private static AudioExportService CreateService(int opusBitrateKbps = 32)
        => new(new AudioFormatDetector(NullLogger.Instance), new CacheSettings { OpusBitrateKbps = opusBitrateKbps }, NullLogger.Instance);

    private static TextSegment Segment(byte[] audio) => new() { Text = "x", AudioData = audio };

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid():N}{extension}");

    [Test]
    public async Task Export_MixedWavAndOpus_WritesWav48k()
    {
        var wav = TestAudio.SineWav(24000, 500);
        var opus = OpusCodec.EncodeWav(wav);
        var outPath = TempPath(".wav");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(opus), Segment(wav), Segment(opus)], outPath);

            await using var reader = new WaveFileReader(outPath);
            await Assert.That(reader.WaveFormat.Encoding).IsEqualTo(WaveFormatEncoding.Pcm);
            await Assert.That(reader.WaveFormat.SampleRate).IsEqualTo(48000);
            await Assert.That(reader.WaveFormat.Channels).IsEqualTo(1);
            // 3 セグメント × 約 500ms (Opus はプリスキップ等で前後し得る)
            await Assert.That(reader.TotalTime.TotalMilliseconds).IsBetween(1400, 1700);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Export_SingleCachedOpusToWav_WritesWavNotOpus()
    {
        var opus = OpusCodec.EncodeWav(TestAudio.SineWav(24000, 500));
        var outPath = TempPath(".wav");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(opus)], outPath);

            var written = await File.ReadAllBytesAsync(outPath);
            await Assert.That(AudioFormatDetector.DetectFormat(written)).IsEqualTo(AudioFormat.Wav);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Export_MixedSegmentsToOpus_WritesDecodableOpus()
    {
        var wav = TestAudio.SineWav(24000, 500);
        var opus = OpusCodec.EncodeWav(wav);
        var outPath = TempPath(".opus");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(wav), Segment(opus)], outPath);

            var written = await File.ReadAllBytesAsync(outPath);
            await Assert.That(AudioFormatDetector.DetectFormat(written)).IsEqualTo(AudioFormat.Opus);

            using var reader = new AudioFormatDetector(NullLogger.Instance).CreateWaveStream(written);
            await Assert.That(reader.TotalTime.TotalMilliseconds).IsBetween(900, 1200);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Export_ToOpus_UsesConfiguredBitrate()
    {
        // 回帰: Cache.OpusBitrateKbps がキャッシュ保存にしか効かず、--out .opus は常に既定値だった
        var wav = TestAudio.SineWav(24000, 2000, frequency: 3000);
        var lowPath = TempPath(".opus");
        var highPath = TempPath(".opus");

        try
        {
            await CreateService(opusBitrateKbps: 12).ExportSegmentsAsync([Segment(wav)], lowPath);
            await CreateService(opusBitrateKbps: 128).ExportSegmentsAsync([Segment(wav)], highPath);

            await Assert.That(new FileInfo(highPath).Length).IsGreaterThan(new FileInfo(lowPath).Length);
        }
        finally
        {
            File.Delete(lowPath);
            File.Delete(highPath);
        }
    }
}

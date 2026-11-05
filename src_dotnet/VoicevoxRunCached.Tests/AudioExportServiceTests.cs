using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// キャッシュヒット (MP3) と新規合成 (WAV) が混在した状態での --out 出力を検証する。
/// </summary>
public class AudioExportServiceTests
{
    private static AudioExportService CreateService()
        => new(new AudioFormatDetector(), NullLogger.Instance);

    private static TextSegment Segment(byte[] audio) => new() { Text = "x", AudioData = audio };

    [Test]
    [Arguments(32)]
    [Arguments(64)]
    [Arguments(128)]
    public async Task ConvertWavToMp3_UsesRequestedBitrate(int bitrateKbps)
    {
        var mp3 = AudioConversionUtility.ConvertWavToMp3(AudioConversionUtility.CreateMinimalWavData(1000), bitrateKbps);

        using var reader = new Mp3FileReader(new MemoryStream(mp3));
        var frame = reader.ReadNextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.BitRate).IsEqualTo(bitrateKbps * 1000);
    }

    [Test]
    public async Task Export_MixedWavAndMp3_WritesValidWav()
    {
        var wav = AudioConversionUtility.CreateMinimalWavData(500);
        var mp3 = AudioConversionUtility.ConvertWavToMp3(wav);
        var outPath = Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid():N}.wav");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(mp3), Segment(wav), Segment(mp3)], outPath);

            await using var reader = new WaveFileReader(outPath);
            await Assert.That(reader.WaveFormat.Encoding).IsEqualTo(WaveFormatEncoding.Pcm);
            // 3 セグメント × 約 500ms (MP3 はエンコーダ遅延分だけ長くなり得る)
            await Assert.That(reader.TotalTime.TotalMilliseconds).IsGreaterThanOrEqualTo(1400);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Export_SingleCachedMp3ToWav_WritesWavNotMp3()
    {
        var mp3 = AudioConversionUtility.ConvertWavToMp3(AudioConversionUtility.CreateMinimalWavData(500));
        var outPath = Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid():N}.wav");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(mp3)], outPath);

            var written = await File.ReadAllBytesAsync(outPath);
            await Assert.That(AudioFormatDetector.DetectFormat(written)).IsEqualTo(AudioFormat.Wav);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Test]
    public async Task Export_MixedSegmentsToMp3_WritesMp3()
    {
        var wav = AudioConversionUtility.CreateMinimalWavData(500);
        var mp3 = AudioConversionUtility.ConvertWavToMp3(wav);
        var outPath = Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid():N}.mp3");

        try
        {
            await CreateService().ExportSegmentsAsync([Segment(wav), Segment(mp3)], outPath);

            var written = await File.ReadAllBytesAsync(outPath);
            await Assert.That(AudioFormatDetector.DetectFormat(written)).IsEqualTo(AudioFormat.Mp3);
        }
        finally
        {
            File.Delete(outPath);
        }
    }
}

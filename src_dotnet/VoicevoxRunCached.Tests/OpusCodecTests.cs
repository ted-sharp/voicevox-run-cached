using NAudio.Wave;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class OpusCodecTests
{
    [Test]
    [Arguments(24000)] // VOICEVOX
    [Arguments(44100)] // AIVIS Speech 等の Opus 非対応レート (48k にリサンプル)
    [Arguments(48000)]
    [Arguments(22050)]
    public async Task RoundTrip_KeepsDurationAndLoudness(int sampleRate)
    {
        var wav = TestAudio.SineWav(sampleRate, 1000);

        var opus = OpusCodec.EncodeWav(wav);
        var decoded = OpusCodec.DecodeToWav(opus);

        await Assert.That(AudioFormatDetector.DetectFormat(opus)).IsEqualTo(AudioFormat.Opus);
        using var reader = new WaveFileReader(new MemoryStream(decoded));
        await Assert.That(reader.WaveFormat.SampleRate).IsEqualTo(48000);
        await Assert.That(reader.WaveFormat.Channels).IsEqualTo(1);
        await Assert.That(reader.TotalTime.TotalMilliseconds).IsBetween(950, 1100);

        // Concentus は 24k エンコード → 24k デコードでほぼ無音になる。48k デコードで音量が保たれていること
        var ratio = TestAudio.Rms(decoded) / TestAudio.Rms(wav);
        await Assert.That(ratio).IsBetween(0.5, 1.5);
    }

    [Test]
    public async Task Encode_Stereo_IsDownmixedToMono()
    {
        var opus = OpusCodec.EncodeWav(TestAudio.SineWav(48000, 500, channels: 2));

        using var reader = new WaveFileReader(new MemoryStream(OpusCodec.DecodeToWav(opus)));
        await Assert.That(reader.WaveFormat.Channels).IsEqualTo(1);
        await Assert.That(reader.TotalTime.TotalMilliseconds).IsGreaterThan(400);
    }

    [Test]
    public async Task Encode_HigherBitrate_ProducesLargerFile()
    {
        var wav = TestAudio.SineWav(24000, 2000, frequency: 3000);

        var low = OpusCodec.EncodeWav(wav, 12).Length;
        var high = OpusCodec.EncodeWav(wav, 128).Length;

        await Assert.That(high).IsGreaterThan(low);
    }

    [Test]
    public async Task Encode_SmallerThanWav()
    {
        var wav = TestAudio.SineWav(24000, 2000);

        var opus = OpusCodec.EncodeWav(wav);

        // 24kHz 16bit mono WAV = 384kbps。32kbps Opus なら 1/5 未満になるはず
        await Assert.That(opus.Length).IsLessThan(wav.Length / 5);
    }
}

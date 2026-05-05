using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class AudioFormatDetectorTests
{
    [Test]
    public async Task DetectFormat_WavHeader_ReturnsWav()
    {
        // RIFF....WAVE
        var header = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 };

        await Assert.That(AudioFormatDetector.DetectFormat(header)).IsEqualTo(AudioFormat.Wav);
    }

    [Test]
    public async Task DetectFormat_Mp3FrameSync_ReturnsMp3()
    {
        // 0xFFE0... のフレーム同期
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };

        await Assert.That(AudioFormatDetector.DetectFormat(header)).IsEqualTo(AudioFormat.Mp3);
    }

    [Test]
    public async Task DetectFormat_TooShort_ReturnsUnknown()
    {
        var header = new byte[] { 0x52, 0x49 };

        await Assert.That(AudioFormatDetector.DetectFormat(header)).IsEqualTo(AudioFormat.Unknown);
    }

    [Test]
    public async Task DetectFormat_RandomBytes_ReturnsUnknown()
    {
        var header = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B };

        await Assert.That(AudioFormatDetector.DetectFormat(header)).IsEqualTo(AudioFormat.Unknown);
    }

    [Test]
    public async Task DetectFormat_RiffButNotWave_ReturnsUnknown()
    {
        // RIFF....AVI_ (動画ファイル)
        var header = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x41, 0x56, 0x49, 0x20 };

        await Assert.That(AudioFormatDetector.DetectFormat(header)).IsEqualTo(AudioFormat.Unknown);
    }
}

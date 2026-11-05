using System.Text;
using NAudio.Wave;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声ユーティリティ (テスト・デバイス準備用の WAV 生成)。Opus 変換は <see cref="OpusCodec"/>。
/// </summary>
public static class AudioConversionUtility
{
    /// <summary>
    /// Creates a minimal WAV file with specified parameters
    /// </summary>
    /// <param name="durationMs">Duration in milliseconds</param>
    /// <param name="sampleRate">Sample rate (default: 22050)</param>
    /// <param name="channels">Number of channels (default: 1)</param>
    /// <param name="bitsPerSample">Bits per sample (default: 16)</param>
    /// <param name="generateSilence">Whether to generate silence (true) or low tone (false)</param>
    /// <returns>WAV audio data</returns>
    public static byte[] CreateMinimalWavData(int durationMs, int sampleRate = AudioConstants.DefaultSampleRate, int channels = AudioConstants.DefaultChannels, int bitsPerSample = AudioConstants.DefaultBitsPerSample, bool generateSilence = false)
    {
        var samplesCount = (sampleRate * durationMs) / 1000;
        var dataSize = samplesCount * channels * (bitsPerSample / 8);
        var fileSize = 44 + dataSize - 8;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // WAV header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(fileSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // PCM format chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8)); // Byte rate
        writer.Write((short)(channels * (bitsPerSample / 8))); // Block align
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        // Generate audio data
        if (generateSilence)
        {
            // True silence - all zero samples
            for (int i = 0; i < samplesCount; i++)
            {
                writer.Write((short)0);
            }
        }
        else
        {
            // Very low amplitude sine wave for device warming
            const double frequency = AudioConstants.WarmupFrequencyHz; // A4 note
            const short amplitude = AudioConstants.WarmupAmplitude; // Very low amplitude

            for (int i = 0; i < samplesCount; i++)
            {
                var time = (double)i / sampleRate;
                var sineValue = Math.Sin(2 * Math.PI * frequency * time);
                var sample = (short)(sineValue * amplitude);
                writer.Write(sample);
            }
        }

        return stream.ToArray();
    }
}

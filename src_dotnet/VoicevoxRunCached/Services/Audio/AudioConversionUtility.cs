using System.Text;
using NAudio.Lame;
using NAudio.Wave;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声フォーマット変換とユーティリティ機能を提供するクラス
/// </summary>
public static class AudioConversionUtility
{
    /// <summary>
    /// WAV 音声データを MP3 に変換します。NAudio.Lame による in-memory 変換のため外部プロセス起動を伴いません。
    /// </summary>
    /// <param name="wavData">変換元のWAVデータ</param>
    /// <returns>変換後のMP3データ</returns>
    public static byte[] ConvertWavToMp3(byte[] wavData)
    {
        try
        {
            using var wavStream = new MemoryStream(wavData);
            using var wavReader = new WaveFileReader(wavStream);
            using var mp3Stream = new MemoryStream();

            using (var mp3Writer = new LameMP3FileWriter(mp3Stream, wavReader.WaveFormat, AudioConstants.Mp3BitrateKbps))
            {
                wavReader.CopyTo(mp3Writer);
            }

            return mp3Stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Failed to convert WAV to MP3: {ex.Message}", ex);
        }
    }

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
            const double frequency = AudioConstants.FillerFrequencyHz; // A4 note
            const short amplitude = AudioConstants.FillerAmplitude; // Very low amplitude

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

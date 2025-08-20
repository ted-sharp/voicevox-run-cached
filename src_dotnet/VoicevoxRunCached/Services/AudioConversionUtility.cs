using NAudio.Wave;
using NAudio.MediaFoundation;
using System.Text;

namespace VoicevoxRunCached.Services;

/// <summary>
/// Utility class for common audio conversion operations
/// </summary>
public static class AudioConversionUtility
{
    /// <summary>
    /// Converts WAV audio data to MP3 format
    /// </summary>
    /// <param name="wavData">WAV audio data</param>
    /// <param name="bitrate">MP3 bitrate in bits per second (default: 128000)</param>
    /// <returns>MP3 audio data</returns>
    public static byte[] ConvertWavToMp3(byte[] wavData, int bitrate = 128000)
    {
        try
        {
            using var wavStream = new MemoryStream(wavData);
            using var waveReader = new WaveFileReader(wavStream);
            using var outputStream = new MemoryStream();

            MediaFoundationManager.EnsureInitialized();
            MediaFoundationEncoder.EncodeToMp3(waveReader, outputStream, bitrate);
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert WAV to MP3: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Detects audio format from header bytes
    /// </summary>
    /// <param name="audioData">Audio data to analyze</param>
    /// <returns>Detected format (WAV, MP3, or Unknown)</returns>
    public static AudioFormat DetectFormat(byte[] audioData)
    {
        if (audioData.Length < 12) return AudioFormat.Unknown;

        // Check for WAV header (RIFF....WAVE)
        if (audioData[0] == 'R' && audioData[1] == 'I' && audioData[2] == 'F' && audioData[3] == 'F' &&
            audioData[8] == 'W' && audioData[9] == 'A' && audioData[10] == 'V' && audioData[11] == 'E')
        {
            return AudioFormat.WAV;
        }

        // Check for MP3 header (starts with 0xFF)
        if (audioData[0] == 0xFF && (audioData[1] & 0xE0) == 0xE0)
        {
            return AudioFormat.MP3;
        }

        return AudioFormat.Unknown;
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
    public static byte[] CreateMinimalWavData(int durationMs, int sampleRate = 22050, int channels = 1, int bitsPerSample = 16, bool generateSilence = false)
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
            const double frequency = 440.0; // A4 note
            const short amplitude = 32; // Very low amplitude

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

/// <summary>
/// Audio format enumeration
/// </summary>
public enum AudioFormat
{
    Unknown,
    WAV,
    MP3
}

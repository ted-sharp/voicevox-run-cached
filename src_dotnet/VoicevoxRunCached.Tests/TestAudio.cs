using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// テスト用の音声データ生成。無音だとエンコード→デコードで音が消えても検出できないため正弦波を使う。
/// </summary>
internal static class TestAudio
{
    public static byte[] SineWav(int sampleRate, int durationMs, int channels = 1, double frequency = 440)
    {
        var signal = new SignalGenerator(sampleRate, channels)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = frequency,
            Gain = 0.5,
        }.Take(TimeSpan.FromMilliseconds(durationMs));

        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(sampleRate, 16, channels)))
        {
            var buffer = new float[sampleRate * channels];
            int read;
            while ((read = signal.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.WriteSamples(buffer, 0, read);
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// WAV の RMS (0〜1)。
    /// </summary>
    public static double Rms(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var buffer = new float[4096];
        double sum = 0;
        long count = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                sum += buffer[i] * buffer[i];
            }
            count += read;
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }
}

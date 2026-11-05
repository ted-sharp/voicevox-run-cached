using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// Concentus (純 C#) による Ogg Opus のエンコード/デコード。
/// </summary>
/// <remarks>
/// <para>エンコード: WAV → mono 16bit → (Opus 非対応レートなら 48kHz にリサンプル) → Ogg Opus (VOIP / VBR / Complexity 10)。</para>
/// <para>デコード: 常に <see cref="AudioConstants.OpusDecodeSampleRate"/> (48kHz) mono 16bit の WAV を返す。
/// Concentus 2.2.2 は 24kHz でエンコードしたものを 24kHz でデコードすると出力がほぼ無音になるため、デコードレートは 48kHz 固定。</para>
/// </remarks>
public static class OpusCodec
{
    private const int Channels = 1;

    /// <summary>
    /// WAV バイト列を Ogg Opus に変換する。
    /// </summary>
    public static byte[] EncodeWav(byte[] wavData, int bitrateKbps = AudioConstants.OpusBitrateKbps)
    {
        ArgumentNullException.ThrowIfNull(wavData);

        using var reader = new WaveFileReader(new MemoryStream(wavData));
        var (pcm, sampleRate) = ReadMonoPcm16(reader.ToSampleProvider());
        return EncodePcm(pcm, sampleRate, bitrateKbps);
    }

    /// <summary>
    /// Ogg Opus を 48kHz mono 16bit の WAV バイト列に変換する。
    /// </summary>
    public static byte[] DecodeToWav(byte[] opusData)
    {
        ArgumentNullException.ThrowIfNull(opusData);

        var decoder = OpusCodecFactory.CreateDecoder(AudioConstants.OpusDecodeSampleRate, Channels);
        var reader = new OpusOggReadStream(decoder, new MemoryStream(opusData));

        var format = new WaveFormat(AudioConstants.OpusDecodeSampleRate, 16, Channels);
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, format))
        {
            while (reader.HasNextPacket)
            {
                var packet = reader.DecodeNextPacket();
                if (packet is { Length: > 0 })
                {
                    writer.WriteSamples(packet, 0, packet.Length);
                }
            }
        }
        return output.ToArray();
    }

    private static byte[] EncodePcm(short[] pcm, int sampleRate, int bitrateKbps)
    {
        // 人の声なので VOIP。AUDIO は楽曲向けで子音が滲みやすい (canock での知見)
        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrateKbps * 1000;
        encoder.UseVBR = true;
        encoder.Complexity = 10;

        using var output = new MemoryStream();
        var ogg = new OpusOggWriteStream(encoder, output, null, sampleRate);
        ogg.WriteSamples(pcm, 0, pcm.Length);
        ogg.Finish();
        return output.ToArray();
    }

    /// <summary>
    /// 任意の SampleProvider を mono・Opus 対応レートの 16bit PCM に揃える。
    /// </summary>
    private static (short[] Pcm, int SampleRate) ReadMonoPcm16(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source);
        }
        else if (source.WaveFormat.Channels != 1)
        {
            throw new NotSupportedException($"Unsupported channel count: {source.WaveFormat.Channels}");
        }

        if (!AudioConstants.OpusSupportedSampleRates.Contains(source.WaveFormat.SampleRate))
        {
            source = new WdlResamplingSampleProvider(source, AudioConstants.OpusDecodeSampleRate);
        }

        var samples = new List<short>(source.WaveFormat.SampleRate);
        var buffer = new float[source.WaveFormat.SampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add((short)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * Int16.MaxValue));
            }
        }
        return (samples.ToArray(), source.WaveFormat.SampleRate);
    }
}

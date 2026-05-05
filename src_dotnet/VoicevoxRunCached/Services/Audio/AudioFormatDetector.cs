using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声フォーマットの検出と適切な WaveStream の作成を行うクラス
/// </summary>
public class AudioFormatDetector
{
    private readonly ILogger _logger;

    public AudioFormatDetector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 音声データのヘッダから検出して適切な WaveStream を返す。
    /// </summary>
    public async Task<WaveStream> CreateWaveStreamAsync(byte[] audioData)
    {
        using (var tempStream = new MemoryStream(audioData))
        {
            tempStream.Position = 0;
            var headerBuffer = new byte[12];
            _ = await tempStream.ReadAsync(headerBuffer.AsMemory(0, 12));

            var format = DetectFormat(headerBuffer);
            _logger.LogDebug("音声フォーマットを検出: {Format}", format);

            var audioStream = new MemoryStream(audioData);
            audioStream.Position = 0;

            return format switch
            {
                AudioFormat.Wav => new WaveFileReader(audioStream),
                AudioFormat.Mp3 => new Mp3FileReader(audioStream),
                _ => CreateFallbackWaveStream(audioStream)
            };
        }
    }

    public static AudioFormat DetectFormat(byte[] headerBuffer)
    {
        if (headerBuffer.Length < 12)
        {
            return AudioFormat.Unknown;
        }

        if (IsWavFormat(headerBuffer))
        {
            return AudioFormat.Wav;
        }

        if (IsMp3Format(headerBuffer))
        {
            return AudioFormat.Mp3;
        }

        return AudioFormat.Unknown;
    }

    private static bool IsWavFormat(byte[] headerBuffer)
    {
        return headerBuffer.Length >= 12 &&
               headerBuffer[0] == 'R' && headerBuffer[1] == 'I' &&
               headerBuffer[2] == 'F' && headerBuffer[3] == 'F' &&
               headerBuffer[8] == 'W' && headerBuffer[9] == 'A' &&
               headerBuffer[10] == 'V' && headerBuffer[11] == 'E';
    }

    private static bool IsMp3Format(byte[] headerBuffer)
    {
        return headerBuffer.Length >= 2 &&
               headerBuffer[0] == 0xFF && (headerBuffer[1] & 0xE0) == 0xE0;
    }

    private WaveStream CreateFallbackWaveStream(MemoryStream audioStream)
    {
        try
        {
            audioStream.Position = 0;
            var stream = new Mp3FileReader(audioStream);
            _logger.LogDebug("MP3 フォールバックで Mp3FileReader を作成しました");
            return stream;
        }
        catch
        {
            audioStream.Position = 0;
            var stream = new WaveFileReader(audioStream);
            _logger.LogDebug("WAV フォールバックで WaveFileReader を作成しました");
            return stream;
        }
    }
}

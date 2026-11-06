using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声フォーマットの検出と適切な WaveStream の作成を行うクラス
/// </summary>
public class AudioFormatDetector
{
    private readonly ILogger _logger;

    public AudioFormatDetector(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 音声データのヘッダから検出して適切な WaveStream を返す。
    /// </summary>
    /// <remarks>
    /// 中身は同期処理 (NAudio reader が同期) なので Task は返さない。
    /// 返却した <see cref="WaveStream"/> が内部 <see cref="MemoryStream"/> を所有・破棄するため、
    /// 呼び出し側は WaveStream を Dispose するだけで良い。
    /// </remarks>
    public WaveStream CreateWaveStream(byte[] audioData)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        var format = DetectFormat(audioData);
        _logger.LogDebug("音声フォーマットを検出: {Format}", format);

        return format switch
        {
            AudioFormat.Wav => new WaveFileReader(new MemoryStream(audioData)),
            AudioFormat.Opus => new WaveFileReader(new MemoryStream(OpusCodec.DecodeToWav(audioData))),
            _ => throw new AudioProcessingException(
                ErrorCodes.Audio.AudioFormatError,
                $"Unknown audio format (size: {audioData.Length} bytes)",
                "音声フォーマットを判定できませんでした。",
                "VOICEVOX エンジンの応答または cache ファイルが破損している可能性があります。"),
        };
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

        if (IsOggFormat(headerBuffer))
        {
            return AudioFormat.Opus;
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

    /// <summary>
    /// Ogg コンテナ ("OggS")。本アプリが書く Ogg は Opus のみ。
    /// </summary>
    private static bool IsOggFormat(byte[] headerBuffer)
    {
        return headerBuffer.Length >= 4 &&
               headerBuffer[0] == 'O' && headerBuffer[1] == 'g' &&
               headerBuffer[2] == 'g' && headerBuffer[3] == 'S';
    }
}

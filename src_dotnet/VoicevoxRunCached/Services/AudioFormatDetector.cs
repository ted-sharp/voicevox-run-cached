using NAudio.Wave;
using Serilog;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声フォーマットの検出と適切なWaveStreamの作成を行うクラス
/// </summary>
public class AudioFormatDetector
{
    /// <summary>
    /// 音声データのヘッダーからフォーマットを検出し、適切なWaveStreamを作成します
    /// </summary>
    /// <param name="audioData">音声データ</param>
    /// <returns>適切なWaveStream</returns>
    public async Task<WaveStream> CreateWaveStreamAsync(byte[] audioData)
    {
        // ヘッダー検出用の一時ストリームを作成
        using var tempStream = new MemoryStream(audioData);

        // ヘッダーを読み取ってフォーマットを検出
        tempStream.Position = 0;
        var headerBuffer = new byte[12];
        var bytesRead = await tempStream.ReadAsync(headerBuffer, 0, 12);

        var format = DetectFormat(headerBuffer);
        Log.Debug("音声フォーマットを検出: {Format}", format);

        // 新しいストリームを作成して返す（usingしない）
        var audioStream = new MemoryStream(audioData);
        audioStream.Position = 0;

        return format switch
        {
            AudioFormat.WAV => new WaveFileReader(audioStream),
            AudioFormat.MP3 => new Mp3FileReader(audioStream),
            _ => CreateFallbackWaveStream(audioStream)
        };
    }

    /// <summary>
    /// 音声データのヘッダーからフォーマットを検出します
    /// </summary>
    /// <param name="headerBuffer">ヘッダーバッファ（最低12バイト必要）</param>
    /// <returns>検出された音声フォーマット</returns>
    public static AudioFormat DetectFormat(byte[] headerBuffer)
    {
        if (headerBuffer.Length < 12)
        {
            return AudioFormat.Unknown;
        }

        // WAVフォーマットの検出
        if (IsWavFormat(headerBuffer))
        {
            return AudioFormat.WAV;
        }

        // MP3フォーマットの検出
        if (IsMp3Format(headerBuffer))
        {
            return AudioFormat.MP3;
        }

        return AudioFormat.Unknown;
    }

    /// <summary>
    /// WAVフォーマットかどうかを判定します
    /// </summary>
    private static bool IsWavFormat(byte[] headerBuffer)
    {
        return headerBuffer.Length >= 12 &&
               headerBuffer[0] == 'R' && headerBuffer[1] == 'I' &&
               headerBuffer[2] == 'F' && headerBuffer[3] == 'F' &&
               headerBuffer[8] == 'W' && headerBuffer[9] == 'A' &&
               headerBuffer[10] == 'V' && headerBuffer[11] == 'E';
    }

    /// <summary>
    /// MP3フォーマットかどうかを判定します
    /// </summary>
    private static bool IsMp3Format(byte[] headerBuffer)
    {
        return headerBuffer.Length >= 2 &&
               headerBuffer[0] == 0xFF && (headerBuffer[1] & 0xE0) == 0xE0;
    }

    /// <summary>
    /// フォーマットが不明な場合のフォールバック処理
    /// MP3を優先して試行し、失敗した場合はWAVとして処理
    /// </summary>
    private WaveStream CreateFallbackWaveStream(MemoryStream audioStream)
    {
        try
        {
            audioStream.Position = 0;
            var stream = new Mp3FileReader(audioStream);
            Log.Debug("MP3 フォールバックで Mp3FileReader を作成しました");
            return stream;
        }
        catch
        {
            // MP3として読み込みに失敗した場合はWAVとして処理
            audioStream.Position = 0;
            var stream = new WaveFileReader(audioStream);
            Log.Debug("WAV フォールバックで WaveFileReader を作成しました");
            return stream;
        }
    }
}

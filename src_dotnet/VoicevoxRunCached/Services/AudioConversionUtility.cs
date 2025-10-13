using System.Text;
using NAudio.Wave;
using Serilog;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声フォーマット変換とユーティリティ機能を提供するクラス
/// </summary>
public static class AudioConversionUtility
{
    /// <summary>
    /// WAV音声データをMP3に変換します
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

            // 一時的なWAVファイルとして保存（NAudioの制約のため）
            var tempWavPath = Path.GetTempFileName();
            try
            {
                WaveFileWriter.CreateWaveFile(tempWavPath, wavReader);

                // FFmpegを使用してWAVからMP3に変換
                var mp3Path = Path.ChangeExtension(tempWavPath, ".mp3");
                ConvertWavToMp3WithFfmpeg(tempWavPath, mp3Path);

                return File.ReadAllBytes(mp3Path);
            }
            finally
            {
                // 一時ファイルの削除
                if (File.Exists(tempWavPath))
                    File.Delete(tempWavPath);
            }
        }
        catch (ArgumentException ex) when (ex.Source == "NAudio.Wave")
        {
            Log.Error(ex, "NAudio処理中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to process audio with NAudio: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            Log.Error(ex, "音声ファイルの読み書き中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to access audio file: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "音声ファイルへのアクセスが拒否されました");
            throw new InvalidOperationException($"Access denied to audio file: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WAVからMP3への変換中に予期しないエラーが発生しました");
            throw new InvalidOperationException($"Unexpected error during WAV to MP3 conversion: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Detects audio format from header bytes
    /// </summary>
    /// <param name="audioData">Audio data to analyze</param>
    /// <returns>Detected format (WAV, MP3, or Unknown)</returns>
    public static AudioFormat DetectFormat(byte[] audioData)
    {
        if (audioData.Length < 12)
            return AudioFormat.Unknown;

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

    /// <summary>
    /// FFmpegを使用してWAVファイルをMP3に変換します
    /// </summary>
    /// <param name="inputWavPath">入力WAVファイルのパス</param>
    /// <param name="outputMp3Path">出力MP3ファイルのパス</param>
    private static void ConvertWavToMp3WithFfmpeg(string inputWavPath, string outputMp3Path)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{inputWavPath}\" -acodec libmp3lame -ab 128k \"{outputMp3Path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("FFmpegプロセスの起動に失敗しました");
            }

            process.WaitForExit(AudioConstants.FfmpegTimeoutMs); // FFmpeg変換のタイムアウト

            if (process.ExitCode != 0)
            {
                var errorOutput = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"FFmpeg変換に失敗しました: {errorOutput}");
            }

            if (!File.Exists(outputMp3Path))
            {
                throw new InvalidOperationException("MP3ファイルの出力に失敗しました");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning(ex, "FFmpegが見つかりません。フォールバックとしてWAVデータをそのまま返します");
            // FFmpegが利用できない場合は、WAVデータをそのまま返す
            File.Copy(inputWavPath, outputMp3Path, true);
        }
        catch (TimeoutException ex)
        {
            Log.Warning(ex, "FFmpeg変換がタイムアウトしました。フォールバックとしてWAVデータをそのまま返します");
            File.Copy(inputWavPath, outputMp3Path, true);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "FFmpeg変換中にファイルアクセスエラーが発生しました。フォールバックとしてWAVデータをそのまま返します");
            File.Copy(inputWavPath, outputMp3Path, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FFmpeg変換中に予期しないエラーが発生しました。フォールバックとしてWAVデータをそのまま返します");
            File.Copy(inputWavPath, outputMp3Path, true);
        }
    }
}

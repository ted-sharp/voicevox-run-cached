using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// 音声ファイルの出力処理を行うクラス
/// </summary>
public class AudioExportService
{
    private readonly AudioFormatDetector _formatDetector;
    private readonly ILogger _logger;

    public AudioExportService(AudioFormatDetector formatDetector, ILogger logger)
    {
        _formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExportSegmentsAsync(List<TextSegment> segments, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("セグメント音声ファイルの出力を開始します: {OutPath}, セグメント数: {Count}", outPath, segments.Count);

            var validSegments = segments.Where(s => s.AudioData != null && s.AudioData.Length > 0).ToList();
            if (validSegments.Count == 0)
            {
                throw new AudioProcessingException(
                    ErrorCodes.Audio.AudioFileExportFailed,
                    "No valid segments to export",
                    "出力可能なセグメントがありません。",
                    "事前に合成を実行し、AudioData が埋まったセグメントを渡してください。");
            }

            var combinedWavData = await CombineWavSegmentsAsync(validSegments.Select(s => s.AudioData!).ToList(), cancellationToken);

            var extension = Path.GetExtension(outPath).ToLowerInvariant();
            byte[] finalAudioData = extension == ".mp3"
                ? AudioConversionUtility.ConvertWavToMp3(combinedWavData)
                : combinedWavData;

            await WriteOutputFileAsync(finalAudioData, outPath, cancellationToken);

            ConsoleHelper.WriteSuccess($"Saved output to: {outPath}");
            _logger.LogInformation("セグメント音声ファイルの出力が完了しました: {OutPath}", outPath);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteLine("Output export was cancelled");
            throw;
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "セグメント音声ファイルの出力に失敗しました: {OutPath}", outPath);
            ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}");
            throw new AudioProcessingException(
                ErrorCodes.Audio.AudioFileExportFailed,
                $"Failed to export combined segments: {outPath}",
                $"音声ファイルの出力に失敗しました: {outPath}",
                ex,
                "出力先ディレクトリの権限と空き容量を確認してください。");
        }
    }

    /// <summary>
    /// 各セグメントを PCM にデコードして 1 本の WAV に結合する。
    /// </summary>
    /// <remarks>
    /// キャッシュヒットしたセグメントは MP3、今回合成したセグメントは WAV なので、
    /// <see cref="AudioFormatDetector.CreateWaveStream"/> で形式を判定してから読む。
    /// 出力フォーマットは先頭セグメントのデコード結果に合わせ、異なるフォーマットが混ざれば例外にする。
    /// 1 件のみの場合も同じ経路を通す (キャッシュ由来の MP3 をそのまま .wav として書き出さないため)。
    /// </remarks>
    private async Task<byte[]> CombineWavSegmentsAsync(List<byte[]> audioDataList, CancellationToken cancellationToken)
    {
        using var outputStream = new MemoryStream();
        WaveFileWriter? writer = null;
        try
        {
            foreach (var audioData in audioDataList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var reader = _formatDetector.CreateWaveStream(audioData);
                writer ??= new WaveFileWriter(outputStream, reader.WaveFormat);

                if (!reader.WaveFormat.Equals(writer.WaveFormat))
                {
                    throw new AudioProcessingException(
                        ErrorCodes.Audio.AudioFormatError,
                        $"Segment format mismatch: {reader.WaveFormat} vs {writer.WaveFormat}",
                        "セグメント間で音声フォーマットが一致しません。",
                        "--no-cache を付けて再実行するか、--clear でキャッシュを削除してください。");
                }

                var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                int bytesRead;
                while ((bytesRead = await reader.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }

            writer!.Flush();
            return outputStream.ToArray();
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private async Task WriteOutputFileAsync(byte[] audioData, string outPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outPath);
        if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(outPath, audioData, cancellationToken);
        _logger.LogDebug("音声ファイルを書き込みました: {OutPath}, サイズ: {Size} bytes", outPath, audioData.Length);
    }
}

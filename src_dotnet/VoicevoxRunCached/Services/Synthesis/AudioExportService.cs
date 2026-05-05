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
    private readonly ILogger _logger;

    public AudioExportService(ILogger logger)
    {
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

    private async Task<byte[]> CombineWavSegmentsAsync(List<byte[]> wavDataList, CancellationToken cancellationToken)
    {
        if (wavDataList.Count == 0)
        {
            throw new AudioProcessingException(
                ErrorCodes.Audio.AudioFormatError,
                "WAV data list is empty",
                "結合対象の WAV データが空です。",
                "合成済みセグメントを 1 件以上渡してください。");
        }

        if (wavDataList.Count == 1)
        {
            return wavDataList[0];
        }

        try
        {
            using var firstStream = new MemoryStream(wavDataList[0]);
            using var firstReader = new WaveFileReader(firstStream);
            var waveFormat = firstReader.WaveFormat;

            foreach (var wavData in wavDataList.Skip(1))
            {
                using var stream = new MemoryStream(wavData);
                using var reader = new WaveFileReader(stream);
                if (!reader.WaveFormat.Equals(waveFormat))
                {
                    _logger.LogWarning("WAVファイルのフォーマットが異なります。最初のフォーマット ({Format}) を使用します", waveFormat);
                }
            }

            using var outputStream = new MemoryStream();
            using var writer = new WaveFileWriter(outputStream, waveFormat);

            foreach (var wavData in wavDataList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var inputStream = new MemoryStream(wavData);
                using var reader = new WaveFileReader(inputStream);

                var buffer = new byte[waveFormat.AverageBytesPerSecond * 4];
                int bytesRead;
                while ((bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await writer.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }
            }

            writer.Flush();
            return outputStream.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAVデータの結合中にエラーが発生しました");
            throw new AudioProcessingException(
                ErrorCodes.Audio.AudioFormatError,
                $"Failed to combine WAV segments: {ex.Message}",
                "WAVデータの結合に失敗しました。",
                ex,
                "セグメントのフォーマットが揃っているか確認してください。");
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

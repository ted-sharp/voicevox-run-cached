using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 音声ファイルの出力処理を行うクラス
/// </summary>
public class AudioExportService
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly ILogger _logger;

    public AudioExportService(VoiceVoxApiClient apiClient, ILogger logger)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExportAudioAsync(VoiceRequest request, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("音声ファイルの出力を開始します: {OutPath}", outPath);

            await _apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

            var audioQuery = await _apiClient.GenerateAudioQueryAsync(request, cancellationToken);
            var wavData = await _apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
            await WriteOutputFileAsync(wavData, outPath, cancellationToken);

            ConsoleHelper.WriteSuccess($"Saved output to: {outPath}", _logger);
            _logger.LogInformation("音声ファイルの出力が完了しました: {OutPath}", outPath);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteLine("Output export was cancelled", _logger);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音声ファイルの出力に失敗しました: {OutPath}", outPath);
            ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}", _logger);
            throw new InvalidOperationException($"音声ファイルの出力に失敗しました: {outPath}", ex);
        }
    }

    public async Task ExportSegmentsAsync(List<TextSegment> segments, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("セグメント音声ファイルの出力を開始します: {OutPath}, セグメント数: {Count}", outPath, segments.Count);

            var validSegments = segments.Where(s => s.AudioData != null && s.AudioData.Length > 0).ToList();
            if (validSegments.Count == 0)
            {
                throw new InvalidOperationException("出力可能なセグメントがありません");
            }

            var combinedWavData = await CombineWavSegmentsAsync(validSegments.Select(s => s.AudioData!).ToList(), cancellationToken);

            var extension = Path.GetExtension(outPath).ToLowerInvariant();
            byte[] finalAudioData;
            if (extension == ".mp3")
            {
                finalAudioData = AudioConversionUtility.ConvertWavToMp3(combinedWavData);
            }
            else
            {
                finalAudioData = combinedWavData;
            }

            await WriteOutputFileAsync(finalAudioData, outPath, cancellationToken);

            ConsoleHelper.WriteSuccess($"Saved output to: {outPath}", _logger);
            _logger.LogInformation("セグメント音声ファイルの出力が完了しました: {OutPath}", outPath);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteLine("Output export was cancelled", _logger);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "セグメント音声ファイルの出力に失敗しました: {OutPath}", outPath);
            ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}", _logger);
            throw new InvalidOperationException($"音声ファイルの出力に失敗しました: {outPath}", ex);
        }
    }

    private async Task<byte[]> CombineWavSegmentsAsync(List<byte[]> wavDataList, CancellationToken cancellationToken)
    {
        if (wavDataList.Count == 0)
        {
            throw new ArgumentException("WAVデータリストが空です", nameof(wavDataList));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAVデータの結合中にエラーが発生しました");
            throw new InvalidOperationException($"WAVデータの結合に失敗しました: {ex.Message}", ex);
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

using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 音声ファイルの出力処理を行うクラス
/// </summary>
public class AudioExportService
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    public AudioExportService(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 音声データをファイルに出力します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>出力処理の完了を表すTask</returns>
    public async Task ExportAudioAsync(VoiceRequest request, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("音声ファイルの出力を開始します: {OutPath}", outPath);

            using var apiClient = new VoiceVoxApiClient(_settings.VoiceVox);
            await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

            var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
            var wavData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
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

    /// <summary>
    /// セグメントの音声データを結合してファイルに出力します
    /// </summary>
    /// <param name="segments">セグメントのリスト</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>出力処理の完了を表すTask</returns>
    public async Task ExportSegmentsAsync(List<TextSegment> segments, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("セグメント音声ファイルの出力を開始します: {OutPath}, セグメント数: {Count}", outPath, segments.Count);

            // 有効なセグメント（音声データがあるもの）をフィルタリング
            var validSegments = segments.Where(s => s.AudioData != null && s.AudioData.Length > 0).ToList();
            if (validSegments.Count == 0)
            {
                throw new InvalidOperationException("出力可能なセグメントがありません");
            }

            // WAVデータを結合
            var combinedWavData = await CombineWavSegmentsAsync(validSegments.Select(s => s.AudioData!).ToList(), cancellationToken);

            // 拡張子に応じて変換
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

    /// <summary>
    /// WAVデータのリストを結合します
    /// </summary>
    /// <param name="wavDataList">WAVデータのリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>結合されたWAVデータ</returns>
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
            // 最初のWAVファイルからフォーマット情報を取得
            using var firstStream = new MemoryStream(wavDataList[0]);
            using var firstReader = new WaveFileReader(firstStream);
            var waveFormat = firstReader.WaveFormat;

            // すべてのWAVファイルが同じフォーマットか確認
            foreach (var wavData in wavDataList.Skip(1))
            {
                using var stream = new MemoryStream(wavData);
                using var reader = new WaveFileReader(stream);
                if (!reader.WaveFormat.Equals(waveFormat))
                {
                    _logger.LogWarning("WAVファイルのフォーマットが異なります。最初のフォーマット ({Format}) を使用します", waveFormat);
                }
            }

            // 結合されたWAVデータをメモリに書き込み
            using var outputStream = new MemoryStream();
            using var writer = new WaveFileWriter(outputStream, waveFormat);

            foreach (var wavData in wavDataList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var inputStream = new MemoryStream(wavData);
                using var reader = new WaveFileReader(inputStream);

                // データを読み込んで書き込み
                var buffer = new byte[waveFormat.AverageBytesPerSecond * 4]; // 4秒分のバッファ
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

    /// <summary>
    /// 音声データをファイルに書き込みます
    /// </summary>
    /// <param name="audioData">音声データ</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>書き込み完了を表すTask</returns>
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

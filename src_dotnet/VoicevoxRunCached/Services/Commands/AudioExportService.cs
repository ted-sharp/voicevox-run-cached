using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 音声ファイルの出力処理を行うクラス
/// </summary>
public class AudioExportService
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public AudioExportService(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            this._logger.LogInformation("音声ファイルの出力を開始します: {OutPath}", outPath);

            using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
            await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

            var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
            var wavData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
            await this.WriteOutputFileAsync(wavData, outPath, cancellationToken);

            ConsoleHelper.WriteSuccess($"Saved output to: {outPath}", this._logger);
            this._logger.LogInformation("音声ファイルの出力が完了しました: {OutPath}", outPath);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteLine("Output export was cancelled", this._logger);
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "音声ファイルの出力に失敗しました: {OutPath}", outPath);
            ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}", this._logger);
            throw;
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
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(outPath, audioData, cancellationToken);
        this._logger.LogDebug("音声ファイルを書き込みました: {OutPath}, サイズ: {Size} bytes", outPath, audioData.Length);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services.Synthesis;

public class FillerManager
{
    private readonly int _defaultSpeaker;
    private readonly FillerSettings _settings;
    private readonly ILogger _logger;
    private string? _lastUsedFiller;

    public FillerManager(FillerSettings settings, VoiceVoxSettings voiceVoxSettings, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(voiceVoxSettings);
        _defaultSpeaker = voiceVoxSettings.DefaultSpeaker;
        _logger = logger ?? NullLogger.Instance;

        _logger.LogInformation("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}",
            _settings.Enabled, _settings.Directory);
    }

    public async Task InitializeFillerCacheAsync(VoiceVoxApiClient apiClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiClient);

        if (!_settings.Enabled)
        {
            _logger.LogInformation("フィラー機能が無効化されているため、初期化をスキップします");
            return;
        }

        Directory.CreateDirectory(_settings.Directory);
        DeleteFiles($"*{DiskCacheStore.LegacyAudioExtension}");
        _logger.LogInformation("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", _settings.FillerTexts.Length);

        using var spinner = new ProgressSpinner("Initializing filler cache...");

        await apiClient.InitializeSpeakerAsync(_defaultSpeaker, cancellationToken);

        int processed = 0;
        int total = _settings.FillerTexts.Length;

        foreach (var fillerText in _settings.FillerTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            _logger.LogDebug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = BuildFillerRequest(fillerText);
            var fillerCachePath = ResolveFillerCachePath(fillerRequest);

            if (File.Exists(fillerCachePath))
            {
                _logger.LogDebug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
                continue;
            }

            try
            {
                var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest, cancellationToken);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId, cancellationToken);

                await File.WriteAllBytesAsync(fillerCachePath, OpusCodec.EncodeWav(audioData), cancellationToken);
                _logger.LogDebug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);
            }
        }

        _logger.LogInformation("フィラーキャッシュの初期化が完了しました - 項目数: {Count}", _settings.FillerTexts.Length);
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _settings.FillerTexts.Length == 0)
        {
            _logger.LogDebug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        string selectedFiller = SelectFillerText();
        _lastUsedFiller = selectedFiller;
        _logger.LogDebug("ランダムフィラー音声を選択: \"{Text}\"", selectedFiller);

        var fillerRequest = BuildFillerRequest(selectedFiller);
        var fillerCachePath = ResolveFillerCachePath(fillerRequest);

        if (!File.Exists(fillerCachePath))
        {
            _logger.LogWarning("フィラーキャッシュファイルが見つかりませんでした (--init で生成してください) - text=\"{Text}\"", selectedFiller);
            return null;
        }

        var data = await File.ReadAllBytesAsync(fillerCachePath, cancellationToken);
        _logger.LogDebug("フィラーキャッシュファイルを読み込みました: {Path} (サイズ: {Size} bytes)",
            Path.GetFileName(fillerCachePath), data.Length);
        return data;
    }

    private string SelectFillerText()
    {
        if (_settings.FillerTexts.Length == 1)
        {
            return _settings.FillerTexts[0];
        }

        var availableFillers = _settings.FillerTexts
            .Where(f => f != _lastUsedFiller)
            .ToArray();

        if (availableFillers.Length == 0)
        {
            availableFillers = _settings.FillerTexts;
        }

        return availableFillers[Random.Shared.Next(availableFillers.Length)];
    }

    private VoiceRequest BuildFillerRequest(string text) => new()
    {
        Text = text,
        SpeakerId = _defaultSpeaker,
        Speed = 1.0,
        Pitch = 0.0,
        Volume = 1.0
    };

    private string ResolveFillerCachePath(VoiceRequest fillerRequest)
    {
        var cacheKey = CacheKeyGenerator.Compute(fillerRequest);
        return Path.Combine(_settings.Directory, $"{cacheKey}{DiskCacheStore.AudioExtension}");
    }

    /// <summary>
    /// フィラーキャッシュを削除する (旧形式の MP3 も含む)。
    /// </summary>
    public void ClearFillerCache()
    {
        DeleteFiles($"*{DiskCacheStore.AudioExtension}");
        DeleteFiles($"*{DiskCacheStore.LegacyAudioExtension}");
    }

    private void DeleteFiles(string searchPattern)
    {
        if (!Directory.Exists(_settings.Directory))
            return;

        foreach (var file in Directory.GetFiles(_settings.Directory, searchPattern))
        {
            try
            { File.Delete(file); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete file: {File}", file); }
        }
    }
}

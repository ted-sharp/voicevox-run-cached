using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// フィラー音声 (合成待ちの隙間に挟む相槌) の生成と取得。
/// </summary>
/// <remarks>
/// フィラーは読み上げと同じ話者の声で再生する (途中で声が変わらないように)。キャッシュキーに話者が含まれるので、
/// 話者ごとに <c>--init -s &lt;id&gt;</c> で生成しておく。生成していない話者ではフィラーを鳴らさない。
/// </remarks>
public class FillerManager
{
    private readonly FillerSettings _settings;
    private readonly int _opusBitrateKbps;
    private readonly ILogger _logger;
    private string? _lastUsedFiller;
    private bool _missingFillerWarned;

    public FillerManager(FillerSettings settings, CacheSettings cacheSettings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(cacheSettings);
        _opusBitrateKbps = cacheSettings.OpusBitrateKbps;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogDebug("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}",
            _settings.Enabled, _settings.Directory);
    }

    /// <param name="apiClient">合成に使う API クライアント</param>
    /// <param name="speakerId">フィラーを生成する話者</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>生成を試みた件数と成否。生成済みでスキップしたフィラーは数えない</returns>
    /// <remarks>
    /// エンジンに届かない失敗 (<see cref="VoiceVoxApiClient.IsEngineUnavailable"/>) が起きたら、残りの未生成分は
    /// API を呼ばずに失敗として数える (<c>--init</c> は読み上げと同じロックを持つので、1 件ごとにリトライ込みの
    /// タイムアウトを待つと、その間 Hooks の読み上げも待たせてしまう)。
    /// </remarks>
    public async Task<SynthesisResult> InitializeFillerCacheAsync(VoiceVoxApiClient apiClient, int speakerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiClient);

        // Filler.Enabled は再生にだけ効く。生成してから有効にする手順 (appsettings.json のコメント・README) なので、
        // 無効のままでも生成する
        Directory.CreateDirectory(_settings.Directory);
        DeleteLegacyFiles();
        _logger.LogInformation("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", _settings.FillerTexts.Length);

        using var spinner = new ProgressSpinner("Initializing filler cache...");

        await apiClient.InitializeSpeakerAsync(speakerId, cancellationToken);

        int processed = 0;
        int attempted = 0;
        int failed = 0;
        int total = _settings.FillerTexts.Length;
        var engineUnavailable = false;

        foreach (var fillerText in _settings.FillerTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            _logger.LogDebug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = BuildFillerRequest(fillerText, speakerId);
            var fillerCachePath = ResolveFillerCachePath(fillerRequest);

            if (File.Exists(fillerCachePath))
            {
                _logger.LogDebug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
                continue;
            }

            attempted++;
            if (engineUnavailable)
            {
                // 生成を中止した後の未生成分
                failed++;
                continue;
            }

            try
            {
                var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest, cancellationToken);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId, cancellationToken);

                await File.WriteAllBytesAsync(fillerCachePath, OpusCodec.EncodeWav(audioData, _opusBitrateKbps), cancellationToken);
                _logger.LogDebug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);

                if (VoiceVoxApiClient.IsEngineUnavailable(ex))
                {
                    engineUnavailable = true;
                    if (processed < total)
                    {
                        _logger.LogWarning("エンジンに接続できない・応答しないため、残りのフィラーの生成を中止します");
                    }
                }
            }
        }

        _logger.LogInformation("フィラーキャッシュの初期化が完了しました - 生成: {Success}, 失敗: {Failure}, 生成済み: {Skipped}",
            attempted - failed, failed, total - attempted);
        return new SynthesisResult(attempted, attempted - failed, failed);
    }

    /// <summary>
    /// v2 以前の MP3 フィラーを削除する。もう読まれないファイルなので、削除できなくてもフィラーの生成は続ける
    /// (本体キャッシュの <see cref="DiskCacheStore.DeleteLegacyFiles"/> と同じく best-effort)。
    /// </summary>
    private void DeleteLegacyFiles()
    {
        foreach (var file in Directory.GetFiles(_settings.Directory, $"*{DiskCacheStore.LegacyAudioExtension}"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "旧形式フィラーの削除に失敗: {Path}", file);
            }
        }
    }

    /// <param name="speakerId">読み上げ中の話者。その話者のフィラーだけを返す</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>フィラーの Ogg Opus。無効・未生成なら null</returns>
    public async Task<byte[]?> GetRandomFillerAudioAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _settings.FillerTexts.Length == 0)
        {
            _logger.LogDebug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        string selectedFiller = SelectFillerText();
        _lastUsedFiller = selectedFiller;
        _logger.LogDebug("ランダムフィラー音声を選択: \"{Text}\"", selectedFiller);

        var fillerRequest = BuildFillerRequest(selectedFiller, speakerId);
        var fillerCachePath = ResolveFillerCachePath(fillerRequest);

        if (!File.Exists(fillerCachePath))
        {
            // 合成待ちのたびに (800ms ごとに) 呼ばれるので、Warning は 1 実行につき 1 回にする
            if (!_missingFillerWarned)
            {
                // 一部だけ未生成のこともある (FillerTexts を追加した・--init が一部失敗した)。--init は生成済みを飛ばすので案内は同じ
                _logger.LogWarning("この話者に未生成のフィラーがあります (--init -s {SpeakerId} で生成できます) - text=\"{Text}\"",
                    speakerId, selectedFiller);
                _missingFillerWarned = true;
            }
            else
            {
                _logger.LogDebug("話者 {SpeakerId} のフィラーが未生成です - text=\"{Text}\"", speakerId, selectedFiller);
            }
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

    private static VoiceRequest BuildFillerRequest(string text, int speakerId) => new()
    {
        Text = text,
        SpeakerId = speakerId,
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
    /// フィラーキャッシュを削除する (旧形式の MP3 も含む)。失敗は CacheException として呼び出し元に伝える。
    /// </summary>
    public Task ClearFillerCacheAsync()
    {
        string[] searchPatterns = [$"*{DiskCacheStore.AudioExtension}", $"*{DiskCacheStore.LegacyAudioExtension}"];
        CacheErrorMapper.WithMapping("フィラーキャッシュのクリア", null, _settings.Directory, _logger, () =>
        {
            if (!Directory.Exists(_settings.Directory))
                return;

            foreach (var file in searchPatterns.SelectMany(p => Directory.GetFiles(_settings.Directory, p)))
            {
                File.Delete(file);
            }
        });
        return Task.CompletedTask;
    }
}

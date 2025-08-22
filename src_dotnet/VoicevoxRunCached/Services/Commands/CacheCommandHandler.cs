using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// キャッシュ関連のコマンド処理を行うクラス
/// </summary>
public class CacheCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public CacheCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 音声キャッシュとフィラーキャッシュをクリアします
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleClearCacheAsync()
    {
        try
        {
            using var spinner = new ProgressSpinner("Clearing audio cache...");
            var cacheManager = new AudioCacheManager(this._settings.Cache);

            await cacheManager.ClearAllCacheAsync();

            // フィラーキャッシュも設定されたフィラーディレクトリを使用してクリア
            var fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);
            await fillerManager.ClearFillerCacheAsync();

            ConsoleHelper.WriteSuccess("Cache cleared successfully!", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error clearing cache: {ex.Message}", this._logger);
            return 1;
        }
    }
}

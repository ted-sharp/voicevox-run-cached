using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// キャッシュ関連のコマンド処理を行うクラス
/// </summary>
public class CacheCommandHandler
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    public CacheCommandHandler(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            var cacheManager = new AudioCacheManager(_settings.Cache);

            cacheManager.ClearAllCache();

            // フィラーキャッシュも設定されたフィラーディレクトリを使用してクリア
            var fillerManager = new FillerManager(_settings.Filler, _settings.VoiceVox.DefaultSpeaker);
            await fillerManager.ClearFillerCacheAsync();

            ConsoleHelper.WriteSuccess("Cache cleared successfully!", _logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error clearing cache: {ex.Message}", _logger);
            return 1;
        }
    }
}

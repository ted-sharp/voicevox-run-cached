using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// キャッシュ関連のコマンド処理を行うクラス
/// </summary>
public class CacheCommandHandler
{
    private readonly AudioCacheManager _cacheManager;
    private readonly FillerManager _fillerManager;
    private readonly ILogger _logger;

    public CacheCommandHandler(AudioCacheManager cacheManager, FillerManager fillerManager, ILogger logger)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
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
            _cacheManager.ClearAllCache();

            await _fillerManager.ClearFillerCacheAsync();

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

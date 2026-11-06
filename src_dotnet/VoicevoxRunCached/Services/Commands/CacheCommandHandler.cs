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

    public async Task<int> HandleClearCacheAsync()
    {
        // スピナーは Dispose 時に自分の行を空白で消すので、成功メッセージより先に破棄する
        // (using var のままだとメッセージを書いた後に消されて表示されない)
        using (new ProgressSpinner("Clearing audio cache..."))
        {
            await _cacheManager.ClearAllCacheAsync();
            await _fillerManager.ClearFillerCacheAsync();
        }

        _logger.LogInformation("キャッシュをクリアしました");
        ConsoleHelper.WriteSuccess("Cache cleared successfully!");
        return 0;
    }
}

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// キャッシュ関連のコマンド処理を行うクラス
/// </summary>
public class CacheCommandHandler
{
    private readonly AudioCacheManager _cacheManager;
    private readonly FillerManager _fillerManager;

    public CacheCommandHandler(AudioCacheManager cacheManager, FillerManager fillerManager)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
    }

    public async Task<int> HandleClearCacheAsync()
    {
        using var spinner = new ProgressSpinner("Clearing audio cache...");
        await _cacheManager.ClearAllCacheAsync();
        await _fillerManager.ClearFillerCacheAsync();

        ConsoleHelper.WriteSuccess("Cache cleared successfully!");
        return 0;
    }
}

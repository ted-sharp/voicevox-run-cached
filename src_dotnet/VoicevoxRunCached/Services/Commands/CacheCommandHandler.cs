using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// キャッシュ関連のコマンド処理を行うクラス
/// </summary>
public class CacheCommandHandler
{
    private readonly AudioCacheManager _cacheManager;
    private readonly FillerManager _fillerManager;
    private readonly SpeechLock _speechLock;
    private readonly ILogger _logger;

    public CacheCommandHandler(AudioCacheManager cacheManager, FillerManager fillerManager, SpeechLock speechLock, ILogger logger)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _speechLock = speechLock ?? throw new ArgumentNullException(nameof(speechLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <remarks>
    /// 読み上げ・<c>--init</c> と同じ <see cref="SpeechLock"/> で直列化する。
    /// 読み上げのキャッシュ保存 (meta.json → .opus の順に書く) の途中で消すと、meta.json の無い .opus が残って
    /// 期限切れの掃除で消えなくなったり、書き込み中のファイルに当たって削除が失敗したりするため。
    /// </remarks>
    public Task<int> HandleClearCacheAsync(CancellationToken cancellationToken = default)
        => _speechLock.RunAsync(ClearLockedAsync, cancellationToken);

    private async Task<int> ClearLockedAsync()
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

using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// エンジンかキャッシュに触れるコマンド (読み上げ・<c>--init</c>・<c>speakers</c>・<c>--clear</c>) を
/// <see cref="InterProcessLock.SpeechLockPath"/> でプロセス間に直列化する入口。
/// </summary>
/// <remarks>
/// <para>ロックの中で処理を実行し、ロックを解放する<b>前に</b> <see cref="VoiceVoxEngineManager.StopEngineIfNotKept"/> を呼ぶ。
/// 解放してから (サービスの Dispose で) 止めると、その間にロックを取った後続のプロセスが動いているエンジンを見て合成を始め、
/// 途中で止められて失敗するため。先に止めておけば、後続はエンジンが止まっているのを見て自分で起動する。</para>
/// <para>エンジンを起動しないコマンド (<c>--clear</c>) では <see cref="VoiceVoxEngineManager.StopEngineIfNotKept"/> は何もしない。
/// それでもここを通すことで、ロックとエンジン停止の書き忘れが起きないようにする。</para>
/// </remarks>
public class SpeechLock
{
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly LockSettings _settings;
    private readonly ILogger _logger;

    public SpeechLock(VoiceVoxEngineManager engineManager, LockSettings settings, ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// ロックを取得して <paramref name="action"/> を実行する。後から来た方は先の処理が終わるまで待つ。
    /// </summary>
    /// <param name="action">ロックの中で行う処理</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <param name="maxWaitSeconds">待つ上限 (秒)。null なら <see cref="LockSettings.MaxWaitSeconds"/> に従う (<c>--max-wait</c> の値を渡す)</param>
    /// <exception cref="VoicevoxRunCachedException">上限までにロックを取得できなかった (<see cref="ErrorCodes.Lock.LockWaitTimeout"/>)</exception>
    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default, int? maxWaitSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var maxWait = ResolveMaxWait(maxWaitSeconds);
        using var speechLock = await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, _logger, maxWait, cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _engineManager.StopEngineIfNotKept();
        }
    }

    /// <summary>
    /// 待つ上限を決める。コマンドラインの値 (<c>--max-wait</c>) を設定値より優先する。どちらも無ければ上限なし (null)。
    /// </summary>
    public TimeSpan? ResolveMaxWait(int? maxWaitSeconds)
        => (maxWaitSeconds ?? _settings.MaxWaitSeconds) is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
}

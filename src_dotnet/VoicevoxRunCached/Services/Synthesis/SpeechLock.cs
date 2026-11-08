using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger;

    public SpeechLock(VoiceVoxEngineManager engineManager, ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// ロックを取得して <paramref name="action"/> を実行する。後から来た方は先の処理が終わるまで待つ。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var speechLock = await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, _logger, cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _engineManager.StopEngineIfNotKept();
        }
    }
}

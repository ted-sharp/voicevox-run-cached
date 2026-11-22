using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// 再生デバイスごとのプロセス間ロック。同じデバイスの再生は 1 つずつ、違うデバイスなら同時に鳴らす。
/// </summary>
/// <remarks>
/// <para>合成 (エンジン・キャッシュ) は <see cref="SpeechLock"/> で直列化し、再生はこのロックで直列化する。
/// 再生をエンジン・キャッシュのロックの外に出すことで、別のデバイスに向けた読み上げが先の再生の終わりを待たずに合成・再生できる。</para>
/// <para>取る順は必ず <see cref="SpeechLock"/> → このロック。このロックを持っている間に <see cref="SpeechLock"/> を待つことはないので、デッドロックしない
/// (このロックを持つプロセスは、合成を終えて <see cref="SpeechLock"/> を手放したか、まだ持っているかのどちらか)。</para>
/// <para>OS 既定 (-1) と、それと同じ実体の番号は別のロックになるので、その組み合わせでは重なる (README に書いてある)。
/// WaveOut の既定のデバイス (WAVE_MAPPER) が実際にどのデバイスかは、OS の設定でいつでも変わるため。</para>
/// </remarks>
public class PlaybackLock
{
    private readonly ILogger _logger;

    public PlaybackLock(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// ロックファイルを置くフォルダ。既定はユーザーごとの一時フォルダ (ほかのプロセスと共有する)。テストで差し替える。
    /// </summary>
    public string LockDirectory { get; init; } = Path.GetTempPath();

    /// <param name="device">WaveOut の index (-1 は OS 既定)</param>
    public string LockFilePath(int device)
        => Path.Combine(LockDirectory, $"VoicevoxRunCached.playback.{(device < 0 ? "default" : device.ToString(System.Globalization.CultureInfo.InvariantCulture))}.lock");

    /// <summary>
    /// <paramref name="device"/> のロックを取得する。
    /// </summary>
    /// <param name="device">WaveOut の index (-1 は OS 既定)</param>
    /// <param name="maxWait">待つ上限 (<see cref="SpeechLock"/> を待った時間と合わせて)。null なら上限なし</param>
    /// <param name="alreadyWaited">先に <see cref="SpeechLock"/> を待った時間</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <exception cref="VoicevoxRunCachedException">上限までに取得できなかった (<see cref="ErrorCodes.Lock.LockWaitTimeout"/>)</exception>
    /// <remarks>
    /// <c>--max-wait</c> は「鳴り始めるまでに待つ時間」の上限なので、2 つのロックを待った時間の合計で数える。合成にかかった時間は数えない。
    /// </remarks>
    public async Task<IDisposable> AcquireAsync(int device, TimeSpan? maxWait, TimeSpan alreadyWaited, CancellationToken cancellationToken)
    {
        var path = LockFilePath(device);
        if (maxWait is not { } limit)
        {
            return await InterProcessLock.AcquireAsync(path, _logger, null, cancellationToken);
        }

        var remaining = limit - alreadyWaited;
        try
        {
            return await InterProcessLock.AcquireAsync(path, _logger, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, cancellationToken);
        }
        catch (VoicevoxRunCachedException ex) when (ex.ErrorCode == ErrorCodes.Lock.LockWaitTimeout)
        {
            // 残りの秒数でなく、利用者が指定した上限を伝える
            throw InterProcessLock.WaitTimeout(path, limit);
        }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// ファイルロックによるプロセス間の排他。
/// </summary>
/// <remarks>
/// <para>ロックファイルを <see cref="FileShare.None"/> で開いている間だけ、他のプロセス (と同一プロセスの他の呼び出し) を待たせる。
/// 解放は返したハンドルの Dispose。プロセスが強制終了されても OS がハンドルを閉じるので、
/// Hooks のタイムアウトで kill されたときも後続が永久に待つことはない。</para>
/// <para>名前付き Mutex を使わないのは、取得したスレッドでしか解放できず、await を挟むと別スレッドからの解放で例外になるため。</para>
/// <para>ロックファイル自体は削除しない (削除中に他プロセスが開こうとするとアクセス拒否になり、排他の判定が難しくなる)。</para>
/// </remarks>
public static class InterProcessLock
{
    /// <summary>
    /// 読み上げ (キャッシュ参照・合成・再生・<c>--out</c>) を直列化するロックファイル。
    /// ユーザーごとの一時フォルダに置くので、別の場所にインストールしたものとも共有される。
    /// </summary>
    public static string SpeechLockPath { get; } = Path.Combine(Path.GetTempPath(), "VoicevoxRunCached.speech.lock");

    private const int RetryIntervalMs = 100;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// ロックを取得するまで、最大 <paramref name="maxWait"/> 待つ。他が保持している間は <see cref="RetryIntervalMs"/> ごとに再試行する。
    /// </summary>
    /// <param name="lockFilePath">ロックファイル</param>
    /// <param name="logger">待機を知らせるログ</param>
    /// <param name="maxWait">待つ上限。null なら上限なし、<see cref="TimeSpan.Zero"/> なら他が保持していればすぐに諦める</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>Dispose で解放されるロック</returns>
    /// <exception cref="VoicevoxRunCachedException">上限までに取得できなかった (<see cref="ErrorCodes.Lock.LockWaitTimeout"/>)</exception>
    /// <exception cref="OperationCanceledException">待機中にキャンセルされた</exception>
    public static async Task<IDisposable> AcquireAsync(string lockFilePath, ILogger logger, TimeSpan? maxWait = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockFilePath);
        ArgumentNullException.ThrowIfNull(logger);

        var waited = Stopwatch.StartNew();
        var waitLogged = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockConflict(ex))
            {
                if (maxWait is { } limit && waited.Elapsed >= limit)
                {
                    throw WaitTimeout(lockFilePath, limit);
                }
                if (!waitLogged)
                {
                    logger.LogInformation("別の VoicevoxRunCached の処理が終わるまで待機します (ロック: {LockFile})", lockFilePath);
                    waitLogged = true;
                }
                await Task.Delay(RetryIntervalMs, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 他のハンドルが開いていることによる失敗か (それ以外の I/O エラーは待っても直らないので呼び出し元へ投げる)。
    /// </summary>
    private static bool IsLockConflict(IOException ex)
        => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static VoicevoxRunCachedException WaitTimeout(string lockFilePath, TimeSpan limit)
    {
        var seconds = limit.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return new VoicevoxRunCachedException(
            errorCode: ErrorCodes.Lock.LockWaitTimeout,
            message: $"Lock was not acquired within {seconds}s: {lockFilePath}",
            userMessage: $"別の読み上げなどが {seconds} 秒以内に終わらなかったため、実行しませんでした。",
            suggestedSolution: "待つ上限は --max-wait か appsettings.json の Lock.MaxWaitSeconds で変えられます。");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 読み上げのプロセス間直列化に使うファイルロック。
/// 排他はハンドル単位なので、同一プロセス内の 2 回の取得でもプロセス間と同じ振る舞いになる。
/// </summary>
public class InterProcessLockTests
{
    private static string NewLockPath() => Path.Combine(Path.GetTempPath(), $"vrc_lock_{Guid.NewGuid():N}.lock");

    [Test]
    public async Task SecondAcquire_WaitsUntilFirstIsReleased()
    {
        var path = NewLockPath();
        try
        {
            var first = await InterProcessLock.AcquireAsync(path, NullLogger.Instance);

            var second = InterProcessLock.AcquireAsync(path, NullLogger.Instance);
            await Task.Delay(300);
            await Assert.That(second.IsCompleted).IsFalse();

            first.Dispose();
            using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(acquired).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Waiting_CanBeCancelled()
    {
        var path = NewLockPath();
        try
        {
            using var first = await InterProcessLock.AcquireAsync(path, NullLogger.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => InterProcessLock.AcquireAsync(path, NullLogger.Instance, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task OtherIoErrors_AreNotRetried()
    {
        // ロック競合以外 (ディレクトリが無い等) は待っても直らないのでそのまま例外にする
        var path = Path.Combine(Path.GetTempPath(), $"vrc_missing_{Guid.NewGuid():N}", "x.lock");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => InterProcessLock.AcquireAsync(path, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

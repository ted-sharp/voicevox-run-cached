using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Exceptions;
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
                () => InterProcessLock.AcquireAsync(path, NullLogger.Instance, cancellationToken: cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MaxWaitZero_FailsImmediatelyWhenHeld()
    {
        var path = NewLockPath();
        try
        {
            using var first = await InterProcessLock.AcquireAsync(path, NullLogger.Instance);

            var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(
                () => InterProcessLock.AcquireAsync(path, NullLogger.Instance, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10)));

            await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Lock.LockWaitTimeout);
            await Assert.That(ErrorCodes.GetExitCode(ex.ErrorCode)).IsEqualTo(9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MaxWait_FailsAfterLimit()
    {
        var path = NewLockPath();
        try
        {
            using var first = await InterProcessLock.AcquireAsync(path, NullLogger.Instance);
            var started = System.Diagnostics.Stopwatch.StartNew();

            await Assert.ThrowsAsync<VoicevoxRunCachedException>(
                () => InterProcessLock.AcquireAsync(path, NullLogger.Instance, TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(10)));

            await Assert.That(started.ElapsedMilliseconds).IsGreaterThanOrEqualTo(300);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MaxWait_SucceedsWhenReleasedInTime()
    {
        var path = NewLockPath();
        try
        {
            var first = await InterProcessLock.AcquireAsync(path, NullLogger.Instance);

            var second = InterProcessLock.AcquireAsync(path, NullLogger.Instance, TimeSpan.FromSeconds(10));
            await Task.Delay(300);
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
    public async Task MaxWait_DoesNotApplyWhenFree()
    {
        // 上限 0 でも、他が持っていなければ取得できる
        var path = NewLockPath();
        try
        {
            using var acquired = await InterProcessLock.AcquireAsync(path, NullLogger.Instance, TimeSpan.Zero);
            await Assert.That(acquired).IsNotNull();
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

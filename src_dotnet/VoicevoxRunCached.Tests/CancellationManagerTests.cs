using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// Ctrl+C の扱い (Console.CancelKeyPress はテストから発火できないので、ハンドラの中身を直接呼ぶ)。
/// </summary>
public class CancellationManagerTests
{
    [Test]
    public async Task FirstRequest_CancelsAndKeepsProcess_SecondRequest_LetsProcessTerminate()
    {
        // 回帰: 何度 Ctrl+C を押しても握りつぶし、終了処理が固まったときに強制終了できなかった
        using var manager = new CancellationManager(NullLogger.Instance);

        await Assert.That(manager.TryRequestCancellation()).IsTrue();
        await Assert.That(manager.Token.IsCancellationRequested).IsTrue();
        await Assert.That(manager.TryRequestCancellation()).IsFalse();
    }

    [Test]
    public async Task RequestAfterDispose_DoesNotThrow()
    {
        var manager = new CancellationManager(NullLogger.Instance);
        manager.Dispose();

        await Assert.That(manager.TryRequestCancellation()).IsFalse();
    }

    [Test]
    public async Task Request_DoesNotRunCallbacksInline()
    {
        // キャンセルの先の処理 (await の続き) をハンドラのスレッドで同期実行すると、
        // その間 lock を持ち続けて 2 回目の Ctrl+C が強制終了できなくなる
        using var manager = new CancellationManager(NullLogger.Instance);
        using var release = new ManualResetEventSlim();
        using var registration = manager.Token.Register(() => release.Wait(TimeSpan.FromSeconds(10)));

        var stopwatch = Stopwatch.StartNew();
        var requested = manager.TryRequestCancellation();
        stopwatch.Stop();
        var secondRequest = manager.TryRequestCancellation();
        release.Set();

        await Assert.That(requested).IsTrue();
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(secondRequest).IsFalse();
    }
}

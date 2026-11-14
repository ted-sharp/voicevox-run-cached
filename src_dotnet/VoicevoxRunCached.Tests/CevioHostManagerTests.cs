using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// CeVIO AI (ホスト) の起動と停止。ホストの代わりに起動するのは、すぐ終わる ping.exe か、しばらく動くバッチファイル。
/// </summary>
public class CevioHostManagerTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    [Test]
    public async Task HostAlreadyStarted_IsNotClosed()
    {
        // 外部で起動済みだったホストには触れない
        var remote = new FakeCevioRemoteService();
        var manager = Create(remote, new CevioSettings { KeepHostRunning = false, HostPath = Ping });

        await manager.EnsureRunningOrThrowAsync(CancellationToken.None);
        manager.StopHostIfNotKept();

        await Assert.That(remote.CloseHostCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 0)]
    public async Task HostStartedByUs_IsClosedUnlessKept(bool keepHostRunning, int expectedCloseCount)
    {
        // 起動前の確認だけ未起動、起動した後は起動済み
        var calls = 0;
        var remote = new FakeCevioRemoteService { HostStarted = () => Interlocked.Increment(ref calls) > 1 };
        var manager = Create(remote, new CevioSettings { KeepHostRunning = keepHostRunning, HostPath = Ping });

        await manager.EnsureRunningOrThrowAsync(CancellationToken.None);
        manager.StopHostIfNotKept();
        manager.StopHostIfNotKept();

        await Assert.That(remote.CloseHostCount).IsEqualTo(expectedCloseCount);
    }

    [Test]
    public async Task AutoStartDisabled_ThrowsEngineNotAvailable()
    {
        var remote = new FakeCevioRemoteService { HostStarted = () => false };
        var manager = Create(remote, new CevioSettings { AutoStartHost = false });

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
    }

    [Test]
    public async Task HostPathNotFound_ThrowsEngineNotAvailable()
    {
        var remote = new FakeCevioRemoteService { HostStarted = () => false };
        var manager = Create(remote, new CevioSettings { HostPath = Path.Combine(Path.GetTempPath(), $"vrc_missing_{Guid.NewGuid():N}.exe") });

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
    }

    [Test]
    public async Task HostExitsDuringStartup_FailsWithoutWaitingForTimeout()
    {
        // 引数なしの ping はすぐ終わる。起動に失敗して終わったホストを、タイムアウト (60 秒) まで待たない
        // (ほかのテストも ping を動かすので、同じ名前のプロセスは無いことにする)
        var remote = new FakeCevioRemoteService { HostStarted = () => false };
        var manager = new CevioHostManager(
            new CevioSettings { HostPath = Ping, StartupTimeoutSeconds = 60, KeepHostRunning = false }, remote, NullLogger.Instance, SystemClock.Instance)
        {
            IsAnotherInstanceRunning = (_, _) => false,
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));
        manager.StopHostIfNotKept();

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));
        await Assert.That(remote.CloseHostCount).IsEqualTo(0);
    }

    [Test]
    public async Task HostHandedToStartingInstance_WaitsAndIsNotClosed()
    {
        // CeVIO AI は 1 つしか動かない。ユーザーが起動中 (まだ IsHostStarted が false) の CeVIO AI があると、起動したプロセスは
        // そちらに任せてすぐ終わる。失敗にせず起動を待ち、自分が起動したものではないので KeepHostRunning: false でも閉じない
        // 起動したプロセス (引数なしの ping) が終わり、ほかのプロセスを確かめた後の 2 回目の確認で起動済みになる
        string? checkedPath = null;
        var checksAfterHandOver = 0;
        var remote = new FakeCevioRemoteService { HostStarted = () => checkedPath != null && ++checksAfterHandOver > 1 };
        var manager = new CevioHostManager(
            new CevioSettings { HostPath = Ping, StartupTimeoutSeconds = 30, KeepHostRunning = false }, remote, NullLogger.Instance, SystemClock.Instance)
        {
            IsAnotherInstanceRunning = (path, _) =>
            {
                checkedPath = path;
                return true;
            },
        };

        await manager.EnsureRunningOrThrowAsync(CancellationToken.None);
        manager.StopHostIfNotKept();

        await Assert.That(checkedPath).IsEqualTo(Ping);
        await Assert.That(remote.CloseHostCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartingInstanceExits_FailsWithoutWaitingForTimeout()
    {
        // 待っていた CeVIO AI (閉じかけだった等) も終わったら、起動のタイムアウト (60 秒) までロックを持ったまま待たない
        var checks = 0;
        var remote = new FakeCevioRemoteService { HostStarted = () => false };
        var manager = new CevioHostManager(
            new CevioSettings { HostPath = Ping, StartupTimeoutSeconds = 60, KeepHostRunning = false }, remote, NullLogger.Instance, SystemClock.Instance)
        {
            // 1 回目は起動中のプロセスがあり、2 回目にはもう無い
            IsAnotherInstanceRunning = (_, _) => Interlocked.Increment(ref checks) == 1,
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));
        manager.StopHostIfNotKept();

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(ex.UserMessage).Contains("起動を待っていた");
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));
        await Assert.That(remote.CloseHostCount).IsEqualTo(0);
    }

    [Test]
    public async Task CloseHostNoResponse_GivesUpWithinTimeout()
    {
        // 固まった CeVIO AI を閉じる呼び出しが戻らなくても、ロックを持ったまま止まらない
        using var block = new ManualResetEventSlim();
        var calls = 0;
        var remote = new FakeCevioRemoteService { HostStarted = () => Interlocked.Increment(ref calls) > 1, BlockClose = block };
        var manager = Create(remote, new CevioSettings { KeepHostRunning = false, HostPath = Ping, SynthesisTimeoutSeconds = 1 });
        try
        {
            await manager.EnsureRunningOrThrowAsync(CancellationToken.None);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            manager.StopHostIfNotKept();

            await Assert.That(remote.CloseHostCount).IsEqualTo(1);
            await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            block.Set();
        }
    }

    [Test]
    [Arguments("", @"C:\Program Files", @"C:\Program Files\CeVIO\CeVIO AI\CeVIO AI.exe")]
    [Arguments(@"D:\CeVIO\CeVIO AI.exe", @"C:\Program Files", @"D:\CeVIO\CeVIO AI.exe")]
    public async Task ResolveHostPath_DefaultsToProgramFiles(string configured, string programFiles, string expected)
    {
        await Assert.That(CevioHostManager.ResolveHostPath(configured, programFiles)).IsEqualTo(expected);
    }

    // 起動している間に他のテストがプロセスを起動すると、そちらにパイプが継承されて誤判定するため単独で実行する
    [Test]
    [NotInParallel]
    public async Task StartHost_DoesNotInheritHandles()
    {
        // ホストが本ツールの継承可能なハンドルを引き継ぐ回帰の検知。CeVIO の StartHost (COM) で起動すると引き継ぐことを実機で確認済み。
        // Hooks は本ツールの stdout / stderr をパイプで受けるので、常駐するホストに書き込み側が残ると Hooks がタイムアウトまで待たされる。
        // - 継承可能なパイプを作り、書き込み側を閉じた後に読み込み側が EOF になるか (= 子に残っていないか) を見る
        // - HostPath: 数秒動くバッチファイルを「ホスト」として起動させる (HostPath は引数を取らないため)
        var script = Path.Combine(Path.GetTempPath(), $"vrc_cevio_host_{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(script, "@ping -n 6 127.0.0.1 > nul\r\n");
        try
        {
            using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
            var calls = 0;
            var remote = new FakeCevioRemoteService { HostStarted = () => Interlocked.Increment(ref calls) > 1 };
            var manager = Create(remote, new CevioSettings { HostPath = script });

            await manager.EnsureRunningOrThrowAsync(CancellationToken.None);
            pipe.DisposeLocalCopyOfClientHandle();

            await Assert.That(await IsClosedWithinAsync(pipe, TimeSpan.FromSeconds(2))).IsTrue();
        }
        finally
        {
            // cmd.exe はバッチファイルを開いたままにしないので、実行中でも消せる
            File.Delete(script);
        }
    }

    private static CevioHostManager Create(FakeCevioRemoteService remote, CevioSettings settings)
        => new(settings, remote, NullLogger.Instance, SystemClock.Instance);

    /// <summary>
    /// 書き込み側がすべて閉じられ、読み込みが EOF になれば true。時間内に閉じなければ false。
    /// </summary>
    private static async Task<bool> IsClosedWithinAsync(Stream readSide, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await readSide.ReadAsync(new byte[1], cts.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

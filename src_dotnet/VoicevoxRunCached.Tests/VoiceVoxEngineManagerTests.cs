using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class VoiceVoxEngineManagerTests
{
    [Test]
    [Arguments("VOICEVOX", @"VOICEVOX\vv-engine\run.exe")]
    [Arguments("AivisSpeech", @"AivisSpeech\AivisSpeech-Engine\run.exe")]
    public async Task EngineRelativePaths_FindEngineBeforeEditor(string productName, string enginePath)
    {
        // 回帰: AivisSpeech のエンジン (AivisSpeech-Engine\run.exe) が候補に無く、EnginePath が空だとエディタ (AivisSpeech.exe) を起動していた
        var paths = VoiceVoxEngineManager.GetEngineRelativePaths(productName);
        var editorPath = Path.Combine(productName, $"{productName}.exe");

        await Assert.That(paths).Contains(enginePath);
        await Assert.That(paths[^1]).IsEqualTo(editorPath);
    }

    [Test]
    public async Task VvppEngine_IsFoundByDefaultEngineUuid()
    {
        // 回帰: VOICEVOX 0.26 でエンジンが %APPDATA%\voicevox\vvpp-engines に移り、vv-engine が無いのでエディタを起動していた
        var appData = Directory.CreateTempSubdirectory("vvpp-test-").FullName;
        try
        {
            var vvppEngines = Path.Combine(appData, "voicevox", "vvpp-engines");
            var otherEngine = Directory.CreateDirectory(Path.Combine(vvppEngines, "Other_Engine+00000000-0000-0000-0000-000000000000"));
            await File.WriteAllTextAsync(Path.Combine(otherEngine.FullName, "run.exe"), "");
            var defaultEngine = Directory.CreateDirectory(
                Path.Combine(vvppEngines, $"VOICEVOX_Engine+{VoiceVoxEngineManager.VoicevoxDefaultEngineUuid}"));
            var runExe = Path.Combine(defaultEngine.FullName, "run.exe");
            await File.WriteAllTextAsync(runExe, "");

            await Assert.That(VoiceVoxEngineManager.FindVoicevoxVvppEngine(appData)).IsEqualTo(runExe);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Test]
    public async Task VvppEngine_NotInstalled_ReturnsNull()
    {
        var appData = Directory.CreateTempSubdirectory("vvpp-test-").FullName;
        try
        {
            await Assert.That(VoiceVoxEngineManager.FindVoicevoxVvppEngine(appData)).IsNull();
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Test]
    public async Task EnsureRunningOrThrow_CancelledDuringStartup_ThrowsOperationCanceled()
    {
        // エンジン起動待ち中の Ctrl+C が「エンジンが利用できません」(終了コード 3) になっていた回帰の検知。
        // - EnginePath: しばらく終了しない ping.exe を「エンジン」として起動させる
        // - BaseUrl: 絶対 URL でないので死活確認は通信せず即失敗し、起動待ちのポーリングに入る
        var settings = StartupSettings(Ping, "-n 30 127.0.0.1");
        using var manager = new VoiceVoxEngineManager(settings, NullLogger.Instance, SystemClock.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.EnsureRunningOrThrowAsync(cts.Token));
    }

    [Test]
    public async Task EngineExitsDuringStartup_FailsWithoutWaitingForTimeout()
    {
        // 起動したエンジンがすぐ終わっても (EnginePath の間違い・起動直後のエラー)、起動のタイムアウト (60 秒) まで
        // ロックを持ったまま待っていた回帰の検知 (ほかのテストも cmd を動かすので、同じ名前のプロセスは無いことにする)
        using var manager = new VoiceVoxEngineManager(StartupSettings(Cmd, "/c exit 1"), NullLogger.Instance, SystemClock.Instance)
        {
            IsAnotherInstanceRunning = (_, _) => false,
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task EngineHandedToStartingInstance_KeepsWaiting()
    {
        // 起動中のエンジン (エディタが起動したもの等) があると、起動したプロセスはポートを取れずに終わる。
        // 同じ名前のプロセスがあれば失敗にせず、その起動を待ち続ける
        string? checkedPath = null;
        using var manager = new VoiceVoxEngineManager(StartupSettings(Cmd, "/c exit 1"), NullLogger.Instance, SystemClock.Instance)
        {
            IsAnotherInstanceRunning = (path, _) =>
            {
                checkedPath = path;
                return true;
            },
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.EnsureRunningOrThrowAsync(cts.Token));

        await Assert.That(checkedPath).IsEqualTo(Cmd);
    }

    [Test]
    public async Task StartingInstanceExits_FailsWithoutWaitingForTimeout()
    {
        // 待っていたエンジンも終わったら、起動のタイムアウト (60 秒) までロックを持ったまま待たない
        var checks = 0;
        using var manager = new VoiceVoxEngineManager(StartupSettings(Cmd, "/c exit 1"), NullLogger.Instance, SystemClock.Instance)
        {
            // 1 回目は起動中のプロセスがあり、2 回目にはもう無い
            IsAnotherInstanceRunning = (_, _) => Interlocked.Increment(ref checks) == 1,
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => manager.EnsureRunningOrThrowAsync(CancellationToken.None));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(checks).IsEqualTo(2);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));
    }

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    /// <summary>
    /// 起動待ちを試す設定。BaseUrl は絶対 URL でないので死活確認は通信せず即失敗し、起動待ちのポーリングに入る。
    /// </summary>
    private static VoiceVoxSettings StartupSettings(string enginePath, string engineArguments) => new()
    {
        BaseUrl = "not-a-url",
        AutoStartEngine = true,
        KeepEngineRunning = false,
        EnginePath = enginePath,
        EngineArguments = engineArguments,
        StartupTimeoutSeconds = 60,
    };

    // 起動している間に他のテストがプロセスを起動すると、そちらにパイプが継承されて誤判定するため単独で実行する
    [Test]
    [NotInParallel]
    public async Task StartEngine_DoesNotInheritHandles()
    {
        // エンジンが本ツールの継承可能なハンドルを引き継いでいた回帰の検知。
        // Hooks は本ツールの stdout / stderr をパイプで受けるので、常駐するエンジンに書き込み側が残ると、
        // 本ツールが終了してもパイプが閉じず、Hooks がタイムアウトまで待たされる。
        // - 継承可能なパイプを作り、書き込み側を閉じた後に読み込み側が EOF になるか (= 子に残っていないか) を見る
        // - EnginePath: しばらく終了しない ping.exe を「エンジン」として起動させる
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        var settings = StartupSettings(Ping, "-n 30 127.0.0.1");
        using (var manager = new VoiceVoxEngineManager(settings, NullLogger.Instance, SystemClock.Instance))
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.EnsureRunningOrThrowAsync(startCts.Token));

            pipe.DisposeLocalCopyOfClientHandle();

            await Assert.That(await IsClosedWithinAsync(pipe, TimeSpan.FromSeconds(3))).IsTrue();
        }
    }

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

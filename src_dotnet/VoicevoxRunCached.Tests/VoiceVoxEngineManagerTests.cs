using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
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
        // - EnginePath: すぐ終了する cmd.exe を「エンジン」として起動させる
        // - BaseUrl: 絶対 URL でないので死活確認は通信せず即失敗し、起動待ちのポーリングに入る
        var settings = new VoiceVoxSettings
        {
            BaseUrl = "not-a-url",
            AutoStartEngine = true,
            KeepEngineRunning = false,
            EnginePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            EngineArguments = "/c exit 0",
            StartupTimeoutSeconds = 30,
        };
        using var manager = new VoiceVoxEngineManager(settings, NullLogger.Instance, SystemClock.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.EnsureRunningOrThrowAsync(cts.Token));
    }

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
        var settings = new VoiceVoxSettings
        {
            BaseUrl = "not-a-url",
            AutoStartEngine = true,
            KeepEngineRunning = false,
            EnginePath = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            EngineArguments = "-n 30 127.0.0.1",
            StartupTimeoutSeconds = 30,
        };
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

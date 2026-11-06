using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class VoiceVoxEngineManagerTests
{
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
}

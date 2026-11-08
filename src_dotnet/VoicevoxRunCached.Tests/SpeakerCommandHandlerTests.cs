using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Commands;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// <c>speakers</c> はエンジンを自動起動しうるので、読み上げと同じロックの中でエンジンの起動・停止を行う。
/// </summary>
public class SpeakerCommandHandlerTests
{
    [Test]
    public async Task Speakers_WaitsForSpeechLock()
    {
        // 回帰: speakers がロックを取らず、読み上げと同時にエンジンを二重に起動しうる・
        // KeepEngineRunning=false で自分が起動したエンジンを読み上げの途中で止めうる状態だった
        using var engine = new FakeEngineServer();

        Task<int> speakers;
        using (await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, NullLogger.Instance))
        {
            speakers = RunSpeakersAsync(engine, engine.CreateSettings());
            await Task.Delay(300);
            await Assert.That(speakers.IsCompleted).IsFalse();
        }

        await Assert.That(await speakers.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);
    }

    [Test]
    public async Task EngineStartedByUs_IsStoppedBeforeReleasingLock()
    {
        // 読み上げ・--init と同じく、KeepEngineRunning=false で自分が起動したエンジンは speakers から戻った時点 (Dispose 前) で止まっていること
        using var engineProcess = new FakeEngineProcess();
        using var engine = new FakeEngineServer(versionAvailable: () => engineProcess.HasStarted);
        var stoppedBeforeDispose = false;

        var exitCode = await RunSpeakersAsync(
            engine,
            engineProcess.CreateSettings(engine.BaseUrl),
            afterCommand: async () => stoppedBeforeDispose = await engineProcess.HasStoppedWithinAsync(TimeSpan.FromSeconds(3)));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stoppedBeforeDispose).IsTrue();
    }

    /// <param name="engine">API の呼び出し先</param>
    /// <param name="voiceVoxSettings">エンジン管理の設定</param>
    /// <param name="afterCommand">speakers から戻った後、エンジン管理を破棄する前に呼ぶ</param>
    private static async Task<int> RunSpeakersAsync(FakeEngineServer engine, VoiceVoxSettings voiceVoxSettings, Func<Task>? afterCommand = null)
    {
        using var engineManager = new VoiceVoxEngineManager(voiceVoxSettings, NullLogger.Instance, SystemClock.Instance);
        using var apiClient = engine.CreateApiClient();
        var handler = new SpeakerCommandHandler(
            engineManager, apiClient, new SpeechLock(engineManager, new LockSettings(), NullLogger.Instance), NullLogger.Instance);

        var exitCode = await handler.HandleSpeakersAsync();
        if (afterCommand != null)
        {
            await afterCommand();
        }
        return exitCode;
    }
}

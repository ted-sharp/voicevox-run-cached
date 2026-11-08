using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Commands;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// <c>--init</c> の終了コード。通常の読み上げと同じく、全部失敗は AUDIO エラー (5)、一部失敗は 8。
/// </summary>
public class InitCommandHandlerTests
{
    [Test]
    public async Task AllFillersSucceed_ReturnsZero()
    {
        using var engine = new FakeEngineServer();

        var exitCode = await RunInitAsync(engine);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task SomeFillersFail_ReturnsPartialFailure()
    {
        using var engine = new FakeEngineServer(audioQueryFails: text => text == "b");

        var exitCode = await RunInitAsync(engine);

        await Assert.That(exitCode).IsEqualTo(ErrorCodes.PartialFailureExitCode);
    }

    [Test]
    public async Task AllFillersFail_ThrowsAudioError()
    {
        // 回帰: 全フィラーの生成に失敗しても成功 (終了コード 0) を返していた
        using var engine = new FakeEngineServer(audioQueryFails: _ => true);

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => RunInitAsync(engine));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Audio.AudioGenerationFailed);
    }

    [Test]
    public async Task NonexistentSpeaker_ThrowsInvalidArguments()
    {
        // 存在しない話者だと /initialize_speaker が 500 (原因の分からないエンジンエラー) になり、リトライの分だけ待たされるので、先に /speakers で弾く
        using var engine = new FakeEngineServer();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => RunInitAsync(engine, speakerId: 999));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    public async Task EngineStartedByUs_IsStoppedBeforeReleasingLock()
    {
        // 回帰: KeepEngineRunning=false で自分が起動したエンジンを、プロセス間ロックを解放した後 (Dispose) に止めていた。
        // その間にロックを取った後続の読み上げが合成の途中でエンジンを失う。--init から戻った時点 (Dispose 前) で止まっていること
        using var engineProcess = new FakeEngineProcess();
        using var engine = new FakeEngineServer(versionAvailable: () => engineProcess.HasStarted);
        var stoppedBeforeDispose = false;

        var exitCode = await RunInitAsync(
            engine,
            voiceVoxSettings: engineProcess.CreateSettings(engine.BaseUrl),
            afterInit: async () => stoppedBeforeDispose = await engineProcess.HasStoppedWithinAsync(TimeSpan.FromSeconds(3)));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stoppedBeforeDispose).IsTrue();
    }

    /// <param name="engine">API の呼び出し先</param>
    /// <param name="speakerId">フィラーを生成する話者</param>
    /// <param name="voiceVoxSettings">エンジン管理の設定。null なら <paramref name="engine"/> に向けた自動起動なしの設定</param>
    /// <param name="afterInit">--init から戻った後、エンジン管理を破棄する前に呼ぶ</param>
    private static async Task<int> RunInitAsync(
        FakeEngineServer engine, int speakerId = 1, VoiceVoxSettings? voiceVoxSettings = null, Func<Task>? afterInit = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        try
        {
            voiceVoxSettings ??= engine.CreateSettings();
            var fillerSettings = new FillerSettings { Enabled = true, Directory = dir, FillerTexts = ["a", "b"] };
            using var engineManager = new VoiceVoxEngineManager(voiceVoxSettings, NullLogger.Instance, SystemClock.Instance);
            using var apiClient = engine.CreateApiClient();
            var fillerManager = new FillerManager(fillerSettings, new CacheSettings(), NullLogger.Instance);
            var handler = new InitCommandHandler(
                engineManager, apiClient, fillerManager, new SpeechLock(engineManager, new LockSettings(), NullLogger.Instance), NullLogger.Instance);

            var exitCode = await handler.HandleInitAsync(speakerId);
            if (afterInit != null)
            {
                await afterInit();
            }
            return exitCode;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}

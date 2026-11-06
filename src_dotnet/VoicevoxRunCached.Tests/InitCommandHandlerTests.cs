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

    private static async Task<int> RunInitAsync(FakeEngineServer engine)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        try
        {
            var voiceVoxSettings = engine.CreateSettings();
            var fillerSettings = new FillerSettings { Enabled = true, Directory = dir, FillerTexts = ["a", "b"] };
            using var engineManager = new VoiceVoxEngineManager(voiceVoxSettings, NullLogger.Instance, SystemClock.Instance);
            using var apiClient = engine.CreateApiClient();
            var fillerManager = new FillerManager(fillerSettings, voiceVoxSettings, NullLogger.Instance);
            var handler = new InitCommandHandler(engineManager, apiClient, fillerManager, NullLogger.Instance);

            return await handler.HandleInitAsync();
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

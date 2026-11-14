using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 全部失敗したときのエラーに、最初の失敗の理由と解決策を添える。
/// </summary>
public class SynthesisResultTests
{
    [Test]
    public async Task ToAllFailedException_WithDomainCause_ShowsCauseAndItsSolution()
    {
        // 回帰: VOICEPEAK の存在しない話者で「全てのセグメントの音声合成に失敗しました」としか出ず、理由がログにしか無かった
        var cause = new VoicevoxRunCachedException(
            ErrorCodes.Engine.EngineProcessError, "failed", "VOICEPEAK の合成に失敗しました: bad exception", suggestedSolution: "話者名を確認してください。");

        var ex = new SynthesisResult(2, 0, 2, FirstError: cause).ToAllFailedException("All failed", "全て失敗しました。");

        await Assert.That(ex.ErrorCode).IsEqualTo(ErrorCodes.Audio.AudioGenerationFailed);
        await Assert.That(ex.UserMessage).IsEqualTo("全て失敗しました。 VOICEPEAK の合成に失敗しました: bad exception");
        await Assert.That(ex.SuggestedSolution).IsEqualTo("話者名を確認してください。");
        await Assert.That(ex.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task ToAllFailedException_WithOtherCause_ShowsMessage()
    {
        var cause = new InvalidDataException("壊れた WAV");

        var ex = new SynthesisResult(1, 0, 1, FirstError: cause).ToAllFailedException("All failed", "全て失敗しました。");

        await Assert.That(ex.UserMessage).IsEqualTo("全て失敗しました。 壊れた WAV");
        await Assert.That(ex.SuggestedSolution).IsEqualTo("合成エンジンの状態とログを確認してください。");
    }

    [Test]
    public async Task ToAllFailedException_WithoutCause_ShowsDefault()
    {
        // --cache-only で読めないキャッシュなど、合成していない失敗
        var ex = new SynthesisResult(1, 0, 1).ToAllFailedException("All failed", "全て失敗しました。");

        await Assert.That(ex.ErrorCode).IsEqualTo(ErrorCodes.Audio.AudioGenerationFailed);
        await Assert.That(ex.UserMessage).IsEqualTo("全て失敗しました。");
        await Assert.That(ex.SuggestedSolution).IsEqualTo("合成エンジンの状態とログを確認してください。");
    }
}

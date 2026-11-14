using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 次のセグメントを待つ 1 回のあいだに鳴らすフィラーの上限 (エンジンの MaxFillersPerWait)。
/// </summary>
public class FillerWaitBudgetTests
{
    [Test]
    [Arguments(null, 100)]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    [Arguments(2, 2)]
    public async Task CanPlay_UpToLimitPerWait(int? maxPerWait, int expectedPlays)
    {
        // 回帰: VOICEPEAK (1 文 10 秒ほど) の合成を待つ間、800ms ごとにフィラーが鳴り続けていた
        var budget = new FillerWaitBudget(maxPerWait);

        var plays = 0;
        while (budget.CanPlay && plays < 100)
        {
            budget.OnFillerPlayed();
            plays++;
        }

        await Assert.That(plays).IsEqualTo(expectedPlays);
    }

    [Test]
    public async Task OnSegmentArrived_ResetsCount()
    {
        // 上限は待ち 1 回ごと。次の文が届いたら、その次の待ちでまた鳴らせる
        var budget = new FillerWaitBudget(1);
        budget.OnFillerPlayed();
        await Assert.That(budget.CanPlay).IsFalse();

        budget.OnSegmentArrived();

        await Assert.That(budget.CanPlay).IsTrue();
    }

    [Test]
    public async Task Defaults_VoicepeakLimitsFillersAndVoicevoxDoesNot()
    {
        // VOICEVOX は合成が速いので従来どおり上限なし。VOICEPEAK は長めのフィラーを 2 回まで
        await Assert.That(new VoiceVoxSettings().MaxFillersPerWait).IsNull();
        await Assert.That(new VoicepeakSettings().MaxFillersPerWait).IsEqualTo(2);
    }

    [Test]
    [Arguments(-1, "a")]
    [Arguments(1, " ")]
    public async Task EngineFillerSettings_InvalidValues_AreRejected(int maxPerWait, string text)
    {
        var voicevox = new VoiceVoxSettingsValidator().Validate(new VoiceVoxSettings { MaxFillersPerWait = maxPerWait, FillerTexts = [text] });
        var voicepeak = new VoicepeakSettingsValidator().Validate(new VoicepeakSettings { MaxFillersPerWait = maxPerWait, FillerTexts = [text] });

        await Assert.That(voicevox.IsValid).IsFalse();
        await Assert.That(voicepeak.IsValid).IsFalse();
    }

    [Test]
    public async Task EngineFillerSettings_ZeroAndUnset_AreValid()
    {
        await Assert.That(new VoiceVoxSettingsValidator().Validate(new VoiceVoxSettings { MaxFillersPerWait = 0 }).IsValid).IsTrue();
        await Assert.That(new VoicepeakSettingsValidator().Validate(new VoicepeakSettings { MaxFillersPerWait = null }).IsValid).IsTrue();
    }
}

using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 合成前の speaker 存在確認 (存在しない ID で /initialize_speaker の 500 + リトライ待ちを避ける)。
/// </summary>
public class SpeakerValidationTests
{
    private static List<Speaker> Speakers() =>
    [
        new Speaker("四国めたん") { Styles = [new SpeakerStyle("あまあま", 0), new SpeakerStyle("ノーマル", 2)] },
        new Speaker("ずんだもん") { Styles = [new SpeakerStyle("ノーマル", 3)] },
    ];

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExistingStyleId_DoesNotThrow(int speakerId)
    {
        TextToSpeechProcessor.EnsureSpeakerExists(Speakers(), speakerId);

        await Assert.That(true).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(99999)]
    public async Task MissingStyleId_ThrowsInvalidArguments(int speakerId)
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
        {
            TextToSpeechProcessor.EnsureSpeakerExists(Speakers(), speakerId);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }
}

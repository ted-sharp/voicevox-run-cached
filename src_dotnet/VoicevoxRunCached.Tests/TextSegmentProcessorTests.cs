using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Tests;

public class TextSegmentProcessorTests
{
    private static TextSegmentProcessor CreateProcessor(int maxSegmentLength = 100)
        => new TextSegmentProcessor(NullLogger.Instance, maxSegmentLength);


    [Test]
    public async Task ProcessText_EmptyString_ReturnsEmpty()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("");

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ProcessText_WhitespaceOnly_ReturnsEmpty()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("   \n  \r\n  ");

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ProcessText_SplitsOnJapaneseFullStop()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("おはよう。こんにちは。");

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ProcessText_SplitsOnQuestionAndExclamation()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("元気ですか？はい元気です！");

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ProcessText_SingleSentenceWithoutTerminator_ReturnsOneSegment()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("単一の文");

        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessText_LongTextSplitsAtMaxSegmentLength()
    {
        var processor = CreateProcessor(maxSegmentLength: 10);

        var longText = new string('あ', 25);

        var result = await processor.ProcessTextAsync(longText);

        await Assert.That(result.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ProcessText_AssignsSpeakerIdToAllSegments()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("文1。文2。", speakerId: 42);

        foreach (var segment in result)
        {
            await Assert.That(segment.SpeakerId).IsEqualTo(42);
        }
    }
}

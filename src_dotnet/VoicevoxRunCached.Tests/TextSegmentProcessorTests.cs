using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services.Synthesis;

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
    public async Task ProcessText_PeriodInsideToken_DoesNotSplit()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("円周率は3.14です。Node.jsで動きます。");

        await Assert.That(result.Select(s => s.Text).ToArray())
            .IsEquivalentTo(new[] { "円周率は3.14です。", "Node.jsで動きます。" });
    }

    [Test]
    public async Task ProcessText_PeriodFollowedBySpaceOrEnd_Splits()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessTextAsync("Hello world. Good morning.");

        await Assert.That(result.Select(s => s.Text).ToArray())
            .IsEquivalentTo(new[] { "Hello world.", "Good morning." });
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
    public async Task ProcessText_OverlongSentence_SplitsAfterReadingComma()
    {
        // 上限 (10) で機械的に切ると「あいうえお、かきくけ」「こさし。」になり、語の途中で読み上げが途切れていた
        var processor = CreateProcessor(maxSegmentLength: 10);

        var result = await processor.ProcessTextAsync("あいうえお、かきくけこさし。");

        await Assert.That(String.Join("|", result.Select(s => s.Text))).IsEqualTo("あいうえお、|かきくけこさし。");
    }

    [Test]
    public async Task ProcessText_OverlongSentence_DoesNotSplitEnglishWords()
    {
        var processor = CreateProcessor(maxSegmentLength: 20);
        const string text = "The quick brown fox jumps over the lazy dog and keeps running";

        var result = await processor.ProcessTextAsync(text);

        await Assert.That(result.Count).IsGreaterThanOrEqualTo(2);
        // 単語の途中で切れていれば、空白で連結しても元の文に戻らない
        await Assert.That(String.Join(" ", result.Select(s => s.Text))).IsEqualTo(text);
    }

    [Test]
    public async Task ProcessText_OverlongSentence_DoesNotSplitSurrogatePair()
    {
        // 上限位置に絵文字 (サロゲートペア) の前半が来ても割らない。割ると不正な UTF-16 が API に送られる
        var processor = CreateProcessor(maxSegmentLength: 10);

        var result = await processor.ProcessTextAsync("あいうえおかきくけ😀こ");

        await Assert.That(String.Join("|", result.Select(s => s.Text))).IsEqualTo("あいうえおかきくけ|😀こ");
    }

    [Test]
    public async Task ProcessText_OverlongSentence_IgnoresBreakInFirstHalf()
    {
        // 前半の読点まで戻ると極端に短いセグメントができるので、上限位置で切る
        var processor = CreateProcessor(maxSegmentLength: 10);

        var result = await processor.ProcessTextAsync("あ、いうえおかきくけこ");

        await Assert.That(String.Join("|", result.Select(s => s.Text))).IsEqualTo("あ、いうえおかきくけ|こ");
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

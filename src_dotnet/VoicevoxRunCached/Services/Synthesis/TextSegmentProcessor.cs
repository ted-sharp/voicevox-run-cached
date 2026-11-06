using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// テキストを文単位のセグメントに分割するクラス。
/// </summary>
public partial class TextSegmentProcessor
{
    private static readonly char[] SentenceEndings = ['。', '！', '？', '!', '?'];
    private static readonly char[] LineBreaks = ['\n', '\r'];

    /// <summary>上限を超えた文を切る位置の候補 (この直後で切る)。空白は <see cref="Char.IsWhiteSpace(char)"/> で別に判定する。</summary>
    private static readonly char[] SoftBreaks = ['、', '，'];

    private readonly int _maxSegmentLength;
    private readonly ILogger _logger;

    public TextSegmentProcessor(ILogger logger, int maxSegmentLength = 100)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxSegmentLength = maxSegmentLength;
    }

    public Task<List<TextSegment>> ProcessTextAsync(string text, int speakerId = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (String.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new List<TextSegment>());
        }

        var rawSegments = SplitTextIntoSegments(text);
        // rawSegments は AddSegmentIfNotEmpty で Trim 済み・空除外済み
        var optimized = rawSegments
            .Select(t => new TextSegment
            {
                Text = WhitespaceRegex().Replace(t, " "),
                SpeakerId = speakerId,
                IsCached = false
            })
            .ToList();

        // 分割数は呼び出し元 (TextToSpeechProcessor) が Information で出すので、こちらは Debug
        _logger.LogDebug("テキスト処理が完了しました - 元のセグメント数: {OriginalCount}, 最適化後: {OptimizedCount}",
            rawSegments.Count, optimized.Count);

        return Task.FromResult(optimized);
    }

    private List<string> SplitTextIntoSegments(string text)
    {
        var segments = new List<string>();
        var currentSegment = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            currentSegment.Append(c);

            if (SentenceEndings.Contains(c)
                || LineBreaks.Contains(c)
                || IsSentenceEndingPeriod(text, i))
            {
                AddSegmentIfNotEmpty(segments, currentSegment);
            }
            else if (currentSegment.Length >= _maxSegmentLength)
            {
                SplitOverlongSegment(segments, currentSegment);
            }
        }

        AddSegmentIfNotEmpty(segments, currentSegment);
        return segments;
    }

    /// <summary>
    /// 上限に達したバッファを区切りの良い位置で切ってセグメントにし、残りはバッファに持ち越す。
    /// </summary>
    private static void SplitOverlongSegment(List<string> segments, StringBuilder buffer)
    {
        int cut = FindSplitPosition(buffer);
        var rest = buffer.ToString(cut, buffer.Length - cut);
        buffer.Length = cut;
        AddSegmentIfNotEmpty(segments, buffer);
        buffer.Append(rest);
    }

    /// <summary>
    /// 後半にある最後の読点・空白の直後を返す (単語や文節の途中で切ると読み上げが不自然になるため)。
    /// 無ければ末尾で切るが、末尾が high surrogate (絵文字などの前半) ならペアを割らないよう 1 文字手前で切る。
    /// </summary>
    /// <remarks>
    /// 前半の区切りまで戻ると極端に短いセグメントができるので、後半だけを探す。
    /// 半角 <c>,</c> は「1,980」を割るので区切りに含めない。
    /// </remarks>
    private static int FindSplitPosition(StringBuilder buffer)
    {
        for (int i = buffer.Length - 1; i >= buffer.Length / 2; i--)
        {
            if (SoftBreaks.Contains(buffer[i]) || Char.IsWhiteSpace(buffer[i]))
            {
                return i + 1;
            }
        }
        return Char.IsHighSurrogate(buffer[^1]) ? buffer.Length - 1 : buffer.Length;
    }

    /// <summary>
    /// 半角ピリオドは直後が空白か文末のときだけ文末とみなす (「3.14」「Node.js」を分割しない)。
    /// </summary>
    private static bool IsSentenceEndingPeriod(string text, int index)
        => text[index] == '.' && (index == text.Length - 1 || Char.IsWhiteSpace(text[index + 1]));

    private static void AddSegmentIfNotEmpty(List<string> segments, StringBuilder buffer)
    {
        var segment = buffer.ToString().Trim();
        if (!String.IsNullOrWhiteSpace(segment))
        {
            segments.Add(segment);
        }
        buffer.Clear();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

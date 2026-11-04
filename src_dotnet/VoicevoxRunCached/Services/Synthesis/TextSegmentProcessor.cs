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

        _logger.LogInformation("テキスト処理が完了しました - 元のセグメント数: {OriginalCount}, 最適化後: {OptimizedCount}",
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
                || IsSentenceEndingPeriod(text, i)
                || currentSegment.Length >= _maxSegmentLength)
            {
                AddSegmentIfNotEmpty(segments, currentSegment);
            }
        }

        AddSegmentIfNotEmpty(segments, currentSegment);
        return segments;
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

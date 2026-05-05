using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// テキストを文単位のセグメントに分割するクラス。
/// </summary>
public partial class TextSegmentProcessor
{
    private static readonly char[] SentenceEndings = ['。', '！', '？', '.', '!', '?'];
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
        var optimized = rawSegments
            .Select(t => OptimizeSegmentText(t))
            .Where(t => !String.IsNullOrWhiteSpace(t))
            .Select(t => new TextSegment
            {
                Text = t,
                Length = t.Length,
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

        foreach (var c in text)
        {
            currentSegment.Append(c);

            if (SentenceEndings.Contains(c)
                || LineBreaks.Contains(c)
                || currentSegment.Length >= _maxSegmentLength)
            {
                AddSegmentIfNotEmpty(segments, currentSegment);
            }
        }

        AddSegmentIfNotEmpty(segments, currentSegment);
        return segments;
    }

    private static void AddSegmentIfNotEmpty(List<string> segments, StringBuilder buffer)
    {
        var segment = buffer.ToString().Trim();
        if (!String.IsNullOrWhiteSpace(segment))
        {
            segments.Add(segment);
        }
        buffer.Clear();
    }

    private static string OptimizeSegmentText(string text)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var optimized = WhitespaceRegex().Replace(text.Trim(), " ");
        return String.IsNullOrWhiteSpace(optimized) ? "。" : optimized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

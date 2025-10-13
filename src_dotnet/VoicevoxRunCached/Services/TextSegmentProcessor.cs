using System.Text;
using Serilog;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// テキストセグメント処理の最適化された実装
/// </summary>
public class TextSegmentProcessor : IDisposable
{
    private readonly int _maxSegmentLength;
    private readonly SemaphoreSlim _semaphore;

    public TextSegmentProcessor(int maxSegmentLength = 100, int maxConcurrentTasks = 4)
    {
        _maxSegmentLength = maxSegmentLength;
        _semaphore = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);

        Log.Information("TextSegmentProcessor を初期化しました - 最大セグメント長: {MaxLength}, 最大並行タスク数: {MaxConcurrent}",
            maxSegmentLength, maxConcurrentTasks);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _semaphore.Dispose();
        }
    }

    /// <summary>
    /// テキストを最適化された方法でセグメントに分割
    /// </summary>
    public async Task<List<TextSegment>> ProcessTextAsync(string text, int speakerId = 1, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(text))
        {
            return new List<TextSegment>();
        }

        try
        {
            // 基本的なセグメント分割
            var segments = SplitTextIntoSegments(text);

            // 並行処理でセグメントを最適化
            var optimizedSegments = await OptimizeSegmentsParallelAsync(segments, speakerId, cancellationToken);

            Log.Information("テキスト処理が完了しました - 元のセグメント数: {OriginalCount}, 最適化後: {OptimizedCount}",
                segments.Count, optimizedSegments.Count);

            return optimizedSegments;
        }
        catch (OperationCanceledException)
        {
            Log.Information("テキスト処理がキャンセルされました");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "テキスト処理中にエラーが発生しました");
            throw new InvalidOperationException("テキスト処理中にエラーが発生しました", ex);
        }
    }

    /// <summary>
    /// テキストをセグメントに分割（最適化された実装）
    /// </summary>
    private List<string> SplitTextIntoSegments(string text)
    {
        var segments = new List<string>();
        var currentSegment = new StringBuilder();
        var sentenceEndings = new[] { '。', '！', '？', '.', '!', '?' };
        var lineBreaks = new[] { '\n', '\r' };

        for (int i = 0; i < text.Length; i++)
        {
            var currentChar = text[i];
            currentSegment.Append(currentChar);

            // センテンス終了文字、改行、または最大長に達した場合の分割
            if (sentenceEndings.Contains(currentChar) ||
                lineBreaks.Contains(currentChar) ||
                currentSegment.Length >= _maxSegmentLength)
            {
                AddSegmentIfNotEmpty(segments, currentSegment);
            }
        }

        // 残りのテキストを追加
        AddSegmentIfNotEmpty(segments, currentSegment);

        return segments;
    }

    /// <summary>
    /// 空でない場合にセグメントをリストに追加
    /// </summary>
    private static void AddSegmentIfNotEmpty(List<string> segments, StringBuilder currentSegment)
    {
        var segment = currentSegment.ToString().Trim();
        if (!String.IsNullOrWhiteSpace(segment))
        {
            segments.Add(segment);
        }
        currentSegment.Clear();
    }

    /// <summary>
    /// 並行処理でセグメントを最適化
    /// </summary>
    private async Task<List<TextSegment>> OptimizeSegmentsParallelAsync(List<string> segments, int speakerId, CancellationToken cancellationToken)
    {
        var optimizedSegments = new List<TextSegment>();
        var tasks = new List<Task<TextSegment>>();

        // セマフォを使用して並行タスク数を制限
        foreach (var segment in segments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var task = ProcessSegmentWithSemaphoreAsync(segment, speakerId, cancellationToken);
            tasks.Add(task);
        }

        // すべてのタスクの完了を待機
        var results = await Task.WhenAll(tasks);

        // 結果を順序通りに並べ替え
        optimizedSegments.AddRange(results);

        return optimizedSegments;
    }

    /// <summary>
    /// セマフォを使用してセグメントを処理
    /// </summary>
    private async Task<TextSegment> ProcessSegmentWithSemaphoreAsync(string text, int speakerId, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            return await Task.Run(() => ProcessSegment(text, speakerId), cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 個別セグメントの処理
    /// </summary>
    private TextSegment ProcessSegment(string text, int speakerId)
    {
        // セグメントの最適化処理
        var optimizedText = OptimizeSegmentText(text);

        return new TextSegment
        {
            Text = optimizedText,
            Position = 0, // 位置は後で計算
            Length = optimizedText.Length,
            SpeakerId = speakerId,
            IsCached = false
        };
    }

    /// <summary>
    /// セグメントテキストの最適化
    /// </summary>
    private string OptimizeSegmentText(string text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return text;

        // 前後の空白を削除
        var optimized = text.Trim();

        // 連続する空白を単一の空白に置換
        optimized = System.Text.RegularExpressions.Regex.Replace(optimized, @"\s+", " ");

        // 空文字列の場合は最小限のテキストを返す
        if (String.IsNullOrWhiteSpace(optimized))
        {
            optimized = "。";
        }

        return optimized;
    }

    /// <summary>
    /// セグメントの位置情報を更新
    /// </summary>
    public void UpdateSegmentPositions(List<TextSegment> segments)
    {
        var currentPosition = 0;

        foreach (var segment in segments)
        {
            segment.Position = currentPosition;
            currentPosition += segment.Length;
        }
    }

    /// <summary>
    /// セグメントの統計情報を取得
    /// </summary>
    public SegmentStatistics GetSegmentStatistics(List<TextSegment> segments)
    {
        if (segments.Count == 0)
        {
            return new SegmentStatistics();
        }

        var totalLength = segments.Sum(s => s.Length);
        var averageLength = (double)totalLength / segments.Count;
        var minLength = segments.Min(s => s.Length);
        var maxLength = segments.Max(s => s.Length);

        return new SegmentStatistics
        {
            TotalSegments = segments.Count,
            TotalLength = totalLength,
            AverageLength = averageLength,
            MinLength = minLength,
            MaxLength = maxLength
        };
    }
}

/// <summary>
/// セグメント統計情報
/// </summary>
public class SegmentStatistics
{
    public int TotalSegments { get; set; }
    public int TotalLength { get; set; }
    public double AverageLength { get; set; }
    public int MinLength { get; set; }
    public int MaxLength { get; set; }
}

using Serilog;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// フィラー音声の挿入処理を行うクラス
/// </summary>
public class FillerInsertionService
{
    private readonly FillerManager? _fillerManager;

    public FillerInsertionService(FillerManager? fillerManager = null)
    {
        _fillerManager = fillerManager;
    }

    /// <summary>
    /// 次のセグメントの準備状況をチェックし、必要に応じてフィラーを挿入します
    /// </summary>
    /// <param name="currentIndex">現在のセグメントインデックス</param>
    /// <param name="segments">全セグメントのリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>フィラー音声データ（挿入不要の場合はnull）</returns>
    public async Task<byte[]?> CheckAndGetFillerAsync(int currentIndex, List<TextSegment> segments, CancellationToken cancellationToken = default)
    {
        if (_fillerManager == null)
        {
            Log.Debug("フィラーマネージャーが設定されていないため、フィラーは挿入されません");
            return null;
        }

        // 最後のセグメントではない場合のみフィラーを検討
        if (currentIndex >= segments.Count - 1)
        {
            Log.Debug("最後のセグメントのため、フィラーは挿入されません");
            return null;
        }

        var nextSegment = segments[currentIndex + 1];
        bool nextSegmentReady = IsSegmentReady(nextSegment);

        if (!nextSegmentReady)
        {
            try
            {
                Log.Debug("次のセグメントの準備が間に合わないため、フィラー音声を取得します");
                var fillerAudio = await _fillerManager.GetRandomFillerAudioAsync();
                if (fillerAudio != null)
                {
                    Log.Information("フィラー音声を取得しました (サイズ: {Size} bytes)", fillerAudio.Length);
                    return fillerAudio;
                }
                else
                {
                    Log.Debug("フィラー音声の取得に失敗しました");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "フィラー音声の取得中にエラーが発生しました");
            }
        }
        else
        {
            Log.Debug("次のセグメントは既に準備完了のため、フィラーは挿入されません (サイズ: {Size} bytes)",
                nextSegment.AudioData?.Length ?? 0);
        }

        return null;
    }

    /// <summary>
    /// セグメントが再生準備完了かどうかを判定します
    /// </summary>
    /// <param name="segment">チェックするセグメント</param>
    /// <returns>準備完了の場合true</returns>
    private static bool IsSegmentReady(TextSegment segment)
    {
        return segment.IsCached &&
               segment.AudioData != null &&
               segment.AudioData.Length > 0;
    }

    /// <summary>
    /// フィラー挿入の必要性を事前に評価します
    /// </summary>
    /// <param name="currentIndex">現在のセグメントインデックス</param>
    /// <param name="segments">全セグメントのリスト</param>
    /// <returns>フィラー挿入が必要な場合true</returns>
    public bool NeedsFillerInsertion(int currentIndex, List<TextSegment> segments)
    {
        if (_fillerManager == null || currentIndex >= segments.Count - 1)
        {
            return false;
        }

        var nextSegment = segments[currentIndex + 1];
        return !IsSegmentReady(nextSegment);
    }

    /// <summary>
    /// フィラー挿入の統計情報を取得します
    /// </summary>
    /// <param name="segments">全セグメントのリスト</param>
    /// <returns>フィラー挿入の統計情報</returns>
    public FillerInsertionStats GetFillerInsertionStats(List<TextSegment> segments)
    {
        if (_fillerManager == null)
        {
            return new FillerInsertionStats { TotalSegments = segments.Count, FillerInsertions = 0 };
        }

        int fillerInsertions = 0;
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (NeedsFillerInsertion(i, segments))
            {
                fillerInsertions++;
            }
        }

        return new FillerInsertionStats
        {
            TotalSegments = segments.Count,
            FillerInsertions = fillerInsertions,
            InsertionRatio = segments.Count > 0 ? (double)fillerInsertions / segments.Count : 0.0
        };
    }
}

/// <summary>
/// フィラー挿入の統計情報
/// </summary>
public class FillerInsertionStats
{
    public int TotalSegments { get; set; }
    public int FillerInsertions { get; set; }
    public double InsertionRatio { get; set; }
}

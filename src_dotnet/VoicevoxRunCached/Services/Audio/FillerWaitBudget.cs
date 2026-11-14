namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 次のセグメントを待つ 1 回のあいだに鳴らしたフィラーを数え、上限 (エンジンの <c>MaxFillersPerWait</c>) に達したかを判定する。
/// </summary>
/// <remarks>
/// 上限が無いと、合成に時間のかかるエンジン (VOICEPEAK は 1 文 10 秒ほど) では待っている間ずっとフィラーが続く。
/// </remarks>
/// <param name="maxPerWait">待ち 1 回の上限。null なら上限なし、0 なら鳴らさない</param>
public sealed class FillerWaitBudget(int? maxPerWait)
{
    private int _played;

    /// <summary>
    /// この待ちでまだフィラーを鳴らせるか。
    /// </summary>
    public bool CanPlay => maxPerWait is not { } max || _played < max;

    /// <summary>
    /// フィラーを鳴らした。
    /// </summary>
    public void OnFillerPlayed() => _played++;

    /// <summary>
    /// セグメントが届いた (次の待ちから数え直す)。
    /// </summary>
    public void OnSegmentArrived() => _played = 0;
}

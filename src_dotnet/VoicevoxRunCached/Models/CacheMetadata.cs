namespace VoicevoxRunCached.Models;

/// <summary>
/// キャッシュエントリのメタデータ (.meta.json に保存)
/// </summary>
public class CacheMetadata
{
    public DateTime CreatedAt { get; set; }
    public string Text { get; set; } = String.Empty;
    public int SpeakerId { get; set; }
    public double Speed { get; set; }
    public double Pitch { get; set; }
    public double Volume { get; set; }

    /// <summary>
    /// Speed/Pitch/Volume を実際に音声へ反映して合成したエントリか。
    /// </summary>
    /// <remarks>
    /// 旧版はパラメータを /audio_query のクエリに付けていたが VOICEVOX が無視するため、
    /// 既定値以外のエントリにも既定値の音声が入っていた。この印の無い (= 旧版で作られた) 既定値以外のエントリは
    /// <see cref="Services.Cache.DiskCacheStore.IsMetadataValid"/> で無効扱いにして作り直す。
    /// </remarks>
    public bool VoiceParametersApplied { get; set; }
}

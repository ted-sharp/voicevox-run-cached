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
}

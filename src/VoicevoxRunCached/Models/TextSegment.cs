namespace VoicevoxRunCached.Models;

public class TextSegment
{
    public string Text { get; set; } = string.Empty;
    public int Position { get; set; }
    public int Length { get; set; }
    public bool IsCached { get; set; }
    public byte[]? AudioData { get; set; }
}
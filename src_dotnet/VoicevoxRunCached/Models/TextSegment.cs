namespace VoicevoxRunCached.Models;

public class TextSegment(string text = "")
{
    public string Text { get; set; } = text;
    public bool IsCached { get; set; }
    public byte[]? AudioData { get; set; }
    public int? SpeakerId { get; set; }
}

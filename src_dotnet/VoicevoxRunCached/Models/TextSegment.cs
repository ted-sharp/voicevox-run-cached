namespace VoicevoxRunCached.Models;

public class TextSegment(string text = "", int length = 0)
{
    public string Text { get; set; } = text;
    public int Length { get; set; } = length;
    public bool IsCached { get; set; }
    public byte[]? AudioData { get; set; }
    public int? SpeakerId { get; set; }
}

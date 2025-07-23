namespace VoicevoxRunCached.Models;

// C# 13 Primary constructor for TextSegment model
public class TextSegment(string text = "", int position = 0, int length = 0)
{
    public string Text { get; set; } = text;
    public int Position { get; set; } = position;
    public int Length { get; set; } = length;
    public bool IsCached { get; set; }
    public byte[]? AudioData { get; set; }
}
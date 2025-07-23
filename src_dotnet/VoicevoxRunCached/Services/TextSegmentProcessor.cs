using System.Text.RegularExpressions;
using NAudio.Wave;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class TextSegmentProcessor
{
    private static readonly Regex SentencePattern = new Regex(@"[。．！？\.\!\?]+", RegexOptions.Compiled);
    private static readonly Regex CleanupPattern = new Regex(@"\s+", RegexOptions.Compiled);

    public static List<TextSegment> SegmentText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<TextSegment>();
        }

        var segments = new List<TextSegment>();
        var sentences = SentencePattern.Split(text);
        var matches = SentencePattern.Matches(text);

        int position = 0;
        for (int i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i].Trim();
            if (string.IsNullOrEmpty(sentence))
                continue;

            // Add punctuation back to the sentence
            if (i < matches.Count)
            {
                sentence += matches[i].Value;
            }

            // Clean up extra whitespace
            sentence = CleanupPattern.Replace(sentence, " ").Trim();

            if (!string.IsNullOrEmpty(sentence))
            {
                segments.Add(new TextSegment
                {
                    Text = sentence,
                    Position = position,
                    Length = sentence.Length
                });
                position += sentence.Length;
            }
        }

        // If no punctuation found, treat entire text as one segment
        if (segments.Count == 0)
        {
            segments.Add(new TextSegment
            {
                Text = text.Trim(),
                Position = 0,
                Length = text.Length
            });
        }

        return segments;
    }

    public static List<byte[]> GetSegmentAudioData(List<byte[]> audioSegments)
    {
        // Return segments for sequential playback instead of concatenation
        return audioSegments.Where(segment => segment.Length > 0).ToList();
    }
}
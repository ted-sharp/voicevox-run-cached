using System.Text.RegularExpressions;
using NAudio.Wave;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public static class TextSegmentProcessor
{
    private static readonly Regex SentencePattern = new Regex(@"[。．！？\.\!\?]+", RegexOptions.Compiled);
    private static readonly Regex CleanupPattern = new Regex(@"\s+", RegexOptions.Compiled);

    public static List<TextSegment> SegmentText(string text, params string[] additionalTexts)
    {
        // C# 13 Collection expression with spread operator
        List<string> allTexts = [text, .. additionalTexts];

        // C# 13 Collection expression initialization
        List<TextSegment> allSegments = [];
        int globalPosition = 0;

        foreach (var currentText in allTexts)
        {
            if (String.IsNullOrWhiteSpace(currentText))
                continue;

            var segments = new List<TextSegment>();
            var sentences = SentencePattern.Split(currentText);
            var matches = SentencePattern.Matches(currentText);

            int position = 0;
            for (int i = 0; i < sentences.Length; i++)
            {
                var sentence = sentences[i].Trim();
                if (String.IsNullOrEmpty(sentence))
                    continue;

                // Add punctuation back to the sentence
                if (i < matches.Count)
                {
                    sentence += matches[i].Value;
                }

                // Clean up extra whitespace
                sentence = CleanupPattern.Replace(sentence, " ").Trim();

                if (!String.IsNullOrEmpty(sentence))
                {
                    segments.Add(new TextSegment
                    {
                        Text = sentence,
                        Position = globalPosition + position,
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
                    Text = currentText.Trim(),
                    Position = globalPosition,
                    Length = currentText.Length
                });
            }

            allSegments.AddRange(segments);
            globalPosition += currentText.Length;
        }

        return allSegments;
    }

    public static List<byte[]> GetSegmentAudioData(params List<byte[]> audioSegments)
    {
        // Return segments for sequential playback instead of concatenation  
        return audioSegments.Where(segment => segment.Length > 0).ToList();
    }

    public static List<TextSegment> ProcessMultipleTexts(params string[] texts)
    {
        // C# 13 Collection expression initialization
        List<TextSegment> allSegments = [];
        int globalPosition = 0;

        foreach (var text in texts)
        {
            var segments = SegmentText(text);
            foreach (var segment in segments)
            {
                segment.Position = globalPosition;
                globalPosition += segment.Length;
                allSegments.Add(segment);
            }
        }

        return allSegments;
    }

    public static List<TextSegment> MergeSegments(params List<TextSegment>[] segmentCollections)
    {
        // C# 13 Collection expression initialization
        List<TextSegment> mergedSegments = [];
        int position = 0;

        foreach (var collection in segmentCollections)
        {
            if (collection != null)
            {
                foreach (var segment in collection)
                {
                    var newSegment = new TextSegment
                    {
                        Text = segment.Text,
                        Position = position,
                        Length = segment.Length,
                        IsCached = segment.IsCached,
                        AudioData = segment.AudioData
                    };
                    mergedSegments.Add(newSegment);
                    position += segment.Length;
                }
            }
        }

        return mergedSegments;
    }
}

namespace VoicevoxRunCached.Models;

public class VoiceRequest
{
    public string Text { get; set; } = string.Empty;
    public int SpeakerId { get; set; }
    public double Speed { get; set; } = 1.0;
    public double Pitch { get; set; } = 0.0;
    public double Volume { get; set; } = 1.0;
}

public class AudioQueryResponse
{
    public double AccentPhrases { get; set; }
    public double SpeedScale { get; set; }
    public double PitchScale { get; set; }
    public double IntonationScale { get; set; }
    public double VolumeScale { get; set; }
    public double PrePhonemeLength { get; set; }
    public double PostPhonemeLength { get; set; }
    public int OutputSamplingRate { get; set; }
    public bool OutputStereo { get; set; }
    public string? Kana { get; set; }
}

public class Speaker
{
    public string Name { get; set; } = string.Empty;
    public string SpeakerUuid { get; set; } = string.Empty;
    public List<SpeakerStyle> Styles { get; set; } = new();
    public string Version { get; set; } = string.Empty;
}

public class SpeakerStyle
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
}
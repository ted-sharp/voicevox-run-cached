namespace VoicevoxRunCached.Models;

// C# 13 Primary constructor for cleaner initialization
public class VoiceRequest(string text = "", int speakerId = 0, double speed = 1.0, double pitch = 0.0, double volume = 1.0)
{
    public string Text { get; set; } = text;
    public int SpeakerId { get; set; } = speakerId;
    public double Speed { get; set; } = speed;
    public double Pitch { get; set; } = pitch;
    public double Volume { get; set; } = volume;
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

// C# 13 Primary constructor for Speaker model
public class Speaker(string name = "", string speakerUuid = "", string version = "")
{
    public string Name { get; set; } = name;
    public string SpeakerUuid { get; set; } = speakerUuid;
    public List<SpeakerStyle> Styles { get; set; } = [];
    public string Version { get; set; } = version;
}

// C# 13 Primary constructor for SpeakerStyle model
public class SpeakerStyle(string name = "", int id = 0)
{
    public string Name { get; set; } = name;
    public int Id { get; set; } = id;
}

namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
}

public class VoiceVoxSettings
{
    public string BaseUrl { get; set; } = "http://localhost:50021";
    public int DefaultSpeaker { get; set; } = 1;
    public int ConnectionTimeout { get; set; } = 30;
}

public class CacheSettings
{
    public string Directory { get; set; } = "./cache/audio/";
    public int ExpirationDays { get; set; } = 30;
    public double MaxSizeGB { get; set; } = 1.0;
}

public class AudioSettings
{
    public int OutputDevice { get; set; } = -1;
    public double Volume { get; set; } = 1.0;
}
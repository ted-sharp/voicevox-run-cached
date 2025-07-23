namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
}

// C# 13 Primary constructor for VoiceVoxSettings
public class VoiceVoxSettings(string baseUrl = "http://localhost:50021", int defaultSpeaker = 1, int connectionTimeout = 30)
{
    public string BaseUrl { get; set; } = baseUrl;
    public int DefaultSpeaker { get; set; } = defaultSpeaker;
    public int ConnectionTimeout { get; set; } = connectionTimeout;
}

// C# 13 Primary constructor for CacheSettings
public class CacheSettings(string directory = "./cache/audio/", int expirationDays = 30, double maxSizeGB = 1.0)
{
    public string Directory { get; set; } = directory;
    public int ExpirationDays { get; set; } = expirationDays;
    public double MaxSizeGB { get; set; } = maxSizeGB;
}

// C# 13 Primary constructor for AudioSettings
public class AudioSettings(int outputDevice = -1, double volume = 1.0)
{
    public int OutputDevice { get; set; } = outputDevice;
    public double Volume { get; set; } = volume;
}
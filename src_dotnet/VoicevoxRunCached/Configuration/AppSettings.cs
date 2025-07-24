namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FillerSettings Filler { get; set; } = new();
}

// C# 13 Primary constructor for VoiceVoxSettings
public class VoiceVoxSettings(string baseUrl = "http://localhost:50021", int defaultSpeaker = 1, int connectionTimeout = 30, bool autoStartEngine = false, string enginePath = "", int startupTimeoutSeconds = 30, string engineArguments = "")
{
    public string BaseUrl { get; set; } = baseUrl;
    public int DefaultSpeaker { get; set; } = defaultSpeaker;
    public int ConnectionTimeout { get; set; } = connectionTimeout;
    public bool AutoStartEngine { get; set; } = autoStartEngine;
    public string EnginePath { get; set; } = enginePath;
    public int StartupTimeoutSeconds { get; set; } = startupTimeoutSeconds;
    public string EngineArguments { get; set; } = engineArguments;
}

// C# 13 Primary constructor for CacheSettings
public class CacheSettings(string directory = "./cache/audio/", int expirationDays = 30, double maxSizeGB = 1.0)
{
    public string Directory { get; set; } = directory;
    public int ExpirationDays { get; set; } = expirationDays;
    public double MaxSizeGB { get; set; } = maxSizeGB;
}

// C# 13 Primary constructor for AudioSettings with device preparation
public class AudioSettings(int outputDevice = -1, double volume = 1.0, bool prepareDevice = false, int preparationDurationMs = 200, double preparationVolume = 0.01)
{
    public int OutputDevice { get; set; } = outputDevice;
    public double Volume { get; set; } = volume;
    
    // Device preparation settings to prevent audio dropouts
    public bool PrepareDevice { get; set; } = prepareDevice;
    public int PreparationDurationMs { get; set; } = preparationDurationMs;
    public double PreparationVolume { get; set; } = preparationVolume; // Very low but audible volume for device warming
}

// C# 13 Primary constructor for FillerSettings
public class FillerSettings(bool enabled = false, string directory = "./cache/filler/", int minDelayMs = 2000, string[] fillerTexts = null)
{
    public bool Enabled { get; set; } = enabled;
    public string Directory { get; set; } = directory;
    public int MinDelayMs { get; set; } = minDelayMs; // Minimum delay before playing filler
    public string[] FillerTexts { get; set; } = fillerTexts ?? [
        "えーっと",
        "あのー",
        "そのー",
        "んー",
        "そうですね",
        "まあ",
        "えー",
        "うーん",
        "ええと",
        "まー",
        "はい",
        "ふむ",
        "おー",
        "んと",
        "あー",
        "うー",
        "んーと",
        "あのう",
        "えーと"
    ];
}
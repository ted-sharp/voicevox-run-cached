namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FillerSettings Filler { get; set; } = new();
}

// C# 13 Primary constructor for VoiceVoxSettings
public class VoiceVoxSettings(string baseUrl = "http://127.0.0.1:50021", int defaultSpeaker = 1, int connectionTimeout = 30, bool autoStartEngine = false, string enginePath = "", int startupTimeoutSeconds = 30, string engineArguments = "", EngineType engineType = EngineType.VOICEVOX, bool keepEngineRunning = true)
{
    public string BaseUrl { get; set; } = baseUrl;
    public int DefaultSpeaker { get; set; } = defaultSpeaker;
    public int ConnectionTimeout { get; set; } = connectionTimeout;
    public bool AutoStartEngine { get; set; } = autoStartEngine;
    public string EnginePath { get; set; } = enginePath;
    public int StartupTimeoutSeconds { get; set; } = startupTimeoutSeconds;
    public string EngineArguments { get; set; } = engineArguments;
    public EngineType EngineType { get; set; } = engineType;
    public bool KeepEngineRunning { get; set; } = keepEngineRunning;
}

public enum EngineType
{
    VOICEVOX,
    AivisSpeech
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
public class FillerSettings(bool enabled = false, string directory = "./cache/filler/", string[]? fillerTexts = null)
{
    public bool Enabled { get; set; } = enabled;
    public string Directory { get; set; } = directory;
    public string[] FillerTexts { get; set; } = fillerTexts ?? [
        "えーっと、",
        "あのー、",
        "あのう、",
        "ええと、",
        "ええっと、",
        "えとえと、"
    ];
}

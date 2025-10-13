namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FillerSettings Filler { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public TestSettings Test { get; set; } = new();
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

    // 新しいプロパティ
    public int TimeoutMs { get; set; } = 30000; // 30秒
    public bool ValidateConnection { get; set; } = false;
}

public enum EngineType
{
    VOICEVOX,
    AivisSpeech
}

// C# 13 Primary constructor for CacheSettings
public class CacheSettings(string directory = "./cache/audio/", int expirationDays = 30, double maxSizeGB = 1.0, bool useExecutableBaseDirectory = false)
{
    public string Directory { get; set; } = directory;
    public int ExpirationDays { get; set; } = expirationDays;
    public double MaxSizeGB { get; set; } = maxSizeGB;

    // When true and Directory is relative, resolve it under the executable directory
    public bool UseExecutableBaseDirectory { get; set; } = useExecutableBaseDirectory;

    // 新しいプロパティ
    public int MemoryCacheSizeMB { get; set; } = 100; // 100MB
}

// C# 13 Primary constructor for AudioSettings with device preparation
public class AudioSettings(int outputDevice = -1, double volume = 1.0, bool prepareDevice = false, int preparationDurationMs = 200, double preparationVolume = 0.01, string outputDeviceId = "")
{
    public int OutputDevice { get; set; } = outputDevice;
    public double Volume { get; set; } = volume;

    // Device preparation settings to prevent audio dropouts
    public bool PrepareDevice { get; set; } = prepareDevice;
    public int PreparationDurationMs { get; set; } = preparationDurationMs;
    public double PreparationVolume { get; set; } = preparationVolume; // Very low but audible volume for device warming

    // WASAPI endpoint ID preference. When set, WasapiOut will be used instead of WaveOutEvent.
    public string OutputDeviceId { get; set; } = outputDeviceId;

    // 新しいプロパティ
    public int DesiredLatency { get; set; } = 100; // 100ms
    public int NumberOfBuffers { get; set; } = 3;
}

// C# 13 Primary constructor for FillerSettings
public class FillerSettings(bool enabled = false, string directory = "./cache/filler/", string[]? fillerTexts = null, bool useExecutableBaseDirectory = false)
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

    public bool UseExecutableBaseDirectory { get; set; } = useExecutableBaseDirectory;

    // 新しいプロパティ
    public int MaxCacheSizeMB { get; set; } = 100; // 100MB
}

// Log level enum for configuration
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

// Logging settings (appsettings.json)
public class LoggingSettings(string level = "Information", string format = "simple")
{
    // Level: Trace|Debug|Information|Warning|Error|Critical|None
    public string Level { get; set; } = level;

    // Format: simple|json
    public string Format { get; set; } = format;

    // 新しいプロパティ
    public bool EnableFileLogging { get; set; } = false;
    public int MaxFileSizeMB { get; set; } = 10; // 10MB
    public int MaxFileCount { get; set; } = 5;
}

// Test settings (appsettings.json)
public class TestSettings(string message = "テストメッセージです。")
{
    public string Message { get; set; } = message;
}

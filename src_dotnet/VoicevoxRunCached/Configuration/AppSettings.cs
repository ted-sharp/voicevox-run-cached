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

// VoiceVox engine settings
public class VoiceVoxSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:50021";
    public int DefaultSpeaker { get; set; } = 1;
    public int ConnectionTimeout { get; set; } = 30;
    public bool AutoStartEngine { get; set; } = false;
    public string EnginePath { get; set; } = "";
    public int StartupTimeoutSeconds { get; set; } = 30;
    public string EngineArguments { get; set; } = "";
    public EngineType EngineType { get; set; } = EngineType.Voicevox;
    public bool KeepEngineRunning { get; set; } = true;
    public int TimeoutMs { get; set; } = 30000; // 30秒
    public bool ValidateConnection { get; set; } = false;
}

public enum EngineType
{
    Voicevox,
    AivisSpeech
}

// Cache settings
public class CacheSettings
{
    public string Directory { get; set; } = "./cache/audio/";
    public int ExpirationDays { get; set; } = 30;
    public double MaxSizeGb { get; set; } = 1.0;
    public bool UseExecutableBaseDirectory { get; set; }
    public int MemoryCacheSizeMb { get; set; } = 100; // 100MB
}

// Audio output settings
public class AudioSettings
{
    public int OutputDevice { get; set; } = -1;
    public double Volume { get; set; } = 1.0;
    public bool PrepareDevice { get; set; } = false;
    public int PreparationDurationMs { get; set; } = 200;
    public double PreparationVolume { get; set; } = 0.01;
    public string OutputDeviceId { get; set; } = "";
    public int DesiredLatency { get; set; } = 100; // 100ms
    public int NumberOfBuffers { get; set; } = 3;
}

// Filler sound settings
public class FillerSettings
{
    public bool Enabled { get; set; } = false;
    public string Directory { get; set; } = "./cache/filler/";

    public string[] FillerTexts { get; set; } =
    [
        "えーっと、",
        "あのー、",
        "あのう、",
        "ええと、",
        "ええっと、",
        "えとえと、"
    ];

    public bool UseExecutableBaseDirectory { get; set; } = false;
    public int MaxCacheSizeMb { get; set; } = 100; // 100MB
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

// Logging settings
public class LoggingSettings
{
    public string Level { get; set; } = "Information";
    public string Format { get; set; } = "simple";
    public bool EnableFileLogging { get; set; } = false;
    public int MaxFileSizeMb { get; set; } = 10; // 10MB
    public int MaxFileCount { get; set; } = 5;
}

// Test settings
public class TestSettings
{
    public string Message { get; set; } = "テストメッセージです。";
}

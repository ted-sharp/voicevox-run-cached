using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FillerSettings Filler { get; set; } = new();
    public LockSettings Lock { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public TestSettings Test { get; set; } = new();
}

// プロセス間ロック (読み上げ・--init・speakers・--clear の直列化) の設定
public class LockSettings
{
    /// <summary>
    /// 先に動いている処理が終わるのを待つ上限 (秒)。null (未設定) なら上限なし、0 なら待たずにすぐ諦める。
    /// 読み上げでは <c>--max-wait</c> で上書きできる。
    /// </summary>
    /// <remarks>
    /// Hooks が続けて発火したときに、古い通知を後からまとめて読み上げないようにするための設定。
    /// 上限を超えたら実行せず、終了コード 9 (<c>LOCK_001</c>) で終わる。
    /// </remarks>
    public int? MaxWaitSeconds { get; set; }
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

    /// <summary>
    /// Opus のビットレート (kbps, VBR)。キャッシュ・フィラー (<c>--init</c>)・<c>--out .opus</c> の保存に使う。
    /// キャッシュキーには含まれないため、変更しても既存キャッシュはそのまま使われる。
    /// </summary>
    public int OpusBitrateKbps { get; set; } = AudioConstants.OpusBitrateKbps;
}

// Audio output settings
public class AudioSettings
{
    public int OutputDevice { get; set; } = -1;
    public double Volume { get; set; } = 1.0;
    public bool PrepareDevice { get; set; } = false;
    public int PreparationDurationMs { get; set; } = 200;
    public double PreparationVolume { get; set; } = 0.01;
    public int DesiredLatency { get; set; } = 100; // 100ms
    public int NumberOfBuffers { get; set; } = 3;
}

// Filler sound settings
public class FillerSettings
{
    public bool Enabled { get; set; } = false;
    public string Directory { get; set; } = "./cache/filler/";

    /// <summary>
    /// フィラーとして読み上げるテキスト。appsettings.json の値をそのまま使う。
    /// </summary>
    /// <remarks>
    /// 初期値を入れてはいけない。ConfigurationBinder は配列プロパティの既存要素に設定値を「追記」するため、
    /// 初期値があると既定 + 設定の重複リストになり、利用者が既定のフィラーを外せなくなる。
    /// </remarks>
    public string[] FillerTexts { get; set; } = [];

    public bool UseExecutableBaseDirectory { get; set; } = false;
}

// Logging settings
public class LoggingSettings
{
    public string Level { get; set; } = "info";
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

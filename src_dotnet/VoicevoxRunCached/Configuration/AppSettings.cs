using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Configuration;

public class AppSettings
{
    public SynthesisSettings Synthesis { get; set; } = new();
    public VoiceVoxSettings VoiceVox { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public FillerSettings Filler { get; set; } = new();
    public LockSettings Lock { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public TestSettings Test { get; set; } = new();
    public VoicepeakSettings Voicepeak { get; set; } = new();
    public CevioSettings Cevio { get; set; } = new();
}

/// <summary>
/// エンジンごとのフィラーの設定 (各エンジンのセクションに置く)。合成の速さがエンジンで違うので、文と回数をエンジンごとに決める。
/// </summary>
public interface IEngineFillerSettings
{
    /// <summary>
    /// このエンジンで使うフィラーの文。空なら <see cref="FillerSettings.FillerTexts"/>。
    /// </summary>
    /// <remarks>
    /// 実装では初期値を入れてはいけない (<see cref="FillerSettings.FillerTexts"/> と同じく、ConfigurationBinder が設定値を追記するため)。
    /// </remarks>
    string[] FillerTexts { get; }

    /// <summary>
    /// 次の文を待つ 1 回のあいだに鳴らすフィラーの上限。null なら上限なし (届くまで繰り返す)、0 ならこのエンジンでは鳴らさない。
    /// 省略したときの既定はエンジンごと (VOICEVOX・CeVIO は null、VOICEPEAK は 2)。上限をなくすには appsettings.json に null を書く。
    /// </summary>
    int? MaxFillersPerWait { get; }
}

// VOICEPEAK (コマンドライン版 voicepeak.exe) の設定
public class VoicepeakSettings : IEngineFillerSettings
{
    /// <summary>
    /// voicepeak.exe のパス。空なら <c>%ProgramFiles%\VOICEPEAK\voicepeak.exe</c>。
    /// </summary>
    public string ExePath { get; set; } = "";

    /// <summary>
    /// <c>--speaker</c> を省略したときの話者 (ナレーター名。<c>"Otomachi Una:joy=50"</c> のように感情も付けられる)。
    /// 空なら <c>--engine voicepeak</c> では <c>--speaker</c> が必須。
    /// </summary>
    public string DefaultSpeaker { get; set; } = "";

    /// <summary>
    /// voicepeak.exe 1 回 (1 文の合成・話者一覧) を待つ上限 (秒)。超えたらプロセスを止めて失敗にする。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    public string[] FillerTexts { get; set; } = [];

    /// <remarks>
    /// 既定は 2。1 文の合成に 10 秒ほどかかり、上限が無いとその間ずっとフィラーが続くため。
    /// </remarks>
    public int? MaxFillersPerWait { get; set; } = 2;
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

// 合成エンジンの選択
public class SynthesisSettings
{
    /// <summary>
    /// 既定で使う合成エンジン。<c>--engine</c> で上書きできる。話者 (<c>--speaker</c>) の既定値は各エンジンのセクションの <c>DefaultSpeaker</c>。
    /// </summary>
    public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Voicevox;
}

/// <summary>
/// 合成エンジンの種類。<c>--engine</c> と <c>Synthesis.Engine</c> の値 (大文字小文字を区別しない)。
/// </summary>
/// <remarks>
/// VOICEVOX 互換のエンジン (AIVIS Speech 等) は <see cref="Voicevox"/> の中で <see cref="VoiceVoxSettings.EngineType"/> で区別する。
/// </remarks>
public enum SpeechEngineKind
{
    Voicevox,
    Voicepeak,
    Cevio,
}

// VoiceVox engine settings
public class VoiceVoxSettings : IEngineFillerSettings
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
    public string[] FillerTexts { get; set; } = [];
    public int? MaxFillersPerWait { get; set; }
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

// CeVIO AI (--engine cevio) の設定
public class CevioSettings : IEngineFillerSettings
{
    /// <summary>
    /// <c>--speaker</c> を省略したときの話者 (キャスト名。感情は <c>キャスト名:感情=値,...</c>)。空ならエンジンに CeVIO を使うとき <c>-s</c> が必須。
    /// </summary>
    public string DefaultSpeaker { get; set; } = "";

    /// <summary>
    /// CeVIO AI が起動していなければ起動する。
    /// </summary>
    public bool AutoStartHost { get; set; } = true;

    /// <summary>
    /// 起動する <c>CeVIO AI.exe</c>。空なら <c>%ProgramFiles%\CeVIO\CeVIO AI\CeVIO AI.exe</c>。
    /// </summary>
    public string HostPath { get; set; } = "";

    /// <summary>
    /// 起動してから使えるようになるまで待つ上限 (秒)。
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 自分で起動した CeVIO AI を、読み上げの後も残す。false なら終わるときに閉じる (外部で起動済みだったものは閉じない)。
    /// </summary>
    public bool KeepHostRunning { get; set; } = true;

    /// <summary>
    /// CeVIO AI の呼び出し (1 文の合成・キャスト一覧・閉じる) を待つ上限 (秒)。CeVIO AI が途中で閉じられると呼び出しが戻らなくなるため。
    /// </summary>
    public int SynthesisTimeoutSeconds { get; set; } = 30;

    public string[] FillerTexts { get; set; } = [];

    public int? MaxFillersPerWait { get; set; }
}

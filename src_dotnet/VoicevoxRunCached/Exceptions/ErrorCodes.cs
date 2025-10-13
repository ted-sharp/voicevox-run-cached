namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// エラーコードの定義
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// エラーコードからカテゴリを取得
    /// </summary>
    public static string GetCategory(string errorCode)
    {
        if (String.IsNullOrEmpty(errorCode))
            return "UNKNOWN";

        var prefix = errorCode.Split('_')[0];
        return prefix switch
        {
            "CONFIG" => "Configuration",
            "ENGINE" => "Engine",
            "CACHE" => "Cache",
            "AUDIO" => "Audio",
            "API" => "API",
            "GEN" => "General",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// エラーコードから説明を取得
    /// </summary>
    public static string GetDescription(string errorCode)
    {
        return errorCode switch
        {
            // Configuration
            Configuration.INVALID_SETTINGS => "設定が無効です",
            Configuration.MISSING_REQUIRED_FIELD => "必須フィールドが不足しています",
            Configuration.INVALID_VALUE => "設定値が無効です",
            Configuration.FILE_NOT_FOUND => "設定ファイルが見つかりません",
            Configuration.PARSE_ERROR => "設定ファイルの解析に失敗しました",

            // Engine
            Engine.ENGINE_NOT_AVAILABLE => "VOICEVOXエンジンが利用できません",
            Engine.ENGINE_STARTUP_FAILED => "VOICEVOXエンジンの起動に失敗しました",
            Engine.ENGINE_TIMEOUT => "VOICEVOXエンジンの起動がタイムアウトしました",
            Engine.ENGINE_COMMUNICATION_ERROR => "VOICEVOXエンジンとの通信に失敗しました",
            Engine.ENGINE_PROCESS_ERROR => "VOICEVOXエンジンプロセスでエラーが発生しました",

            // Cache
            Cache.CACHE_READ_ERROR => "キャッシュの読み込みに失敗しました",
            Cache.CACHE_WRITE_ERROR => "キャッシュの書き込みに失敗しました",
            Cache.CACHE_CORRUPTED => "キャッシュが破損しています",
            Cache.CACHE_FULL => "キャッシュが満杯です",
            Cache.CACHE_PERMISSION_DENIED => "キャッシュへのアクセス権限がありません",

            // Audio
            Audio.AUDIO_GENERATION_FAILED => "音声の生成に失敗しました",
            Audio.AUDIO_PLAYBACK_FAILED => "音声の再生に失敗しました",
            Audio.AUDIO_FORMAT_ERROR => "音声フォーマットが無効です",
            Audio.AUDIO_DEVICE_ERROR => "音声デバイスでエラーが発生しました",
            Audio.AUDIO_ENCODING_FAILED => "音声のエンコードに失敗しました",
            Audio.MediaFoundationInitFailed => "MediaFoundationの初期化に失敗しました",

            // API
            Api.API_REQUEST_FAILED => "APIリクエストに失敗しました",
            Api.API_RESPONSE_INVALID => "APIレスポンスが無効です",
            Api.API_TIMEOUT => "APIリクエストがタイムアウトしました",
            Api.API_AUTHENTICATION_ERROR => "API認証に失敗しました",
            Api.API_RATE_LIMIT_EXCEEDED => "APIレート制限を超過しました",

            // General
            General.UNKNOWN_ERROR => "不明なエラーが発生しました",
            General.INVALID_ARGUMENTS => "無効な引数が指定されました",
            General.OPERATION_CANCELLED => "操作がキャンセルされました",
            General.RESOURCE_NOT_FOUND => "リソースが見つかりません",
            General.PERMISSION_DENIED => "アクセス権限がありません",
            General.NETWORK_ERROR => "ネットワークエラーが発生しました",
            General.TIMEOUT_ERROR => "タイムアウトが発生しました",
            General.INVALID_OPERATION => "無効な操作が試行されました",

            _ => "不明なエラーコードです"
        };
    }

    // 設定関連のエラーコード
    public static class Configuration
    {
        public const string INVALID_SETTINGS = "CONFIG_001";
        public const string MISSING_REQUIRED_FIELD = "CONFIG_002";
        public const string INVALID_VALUE = "CONFIG_003";
        public const string FILE_NOT_FOUND = "CONFIG_004";
        public const string PARSE_ERROR = "CONFIG_005";
    }

    // VOICEVOXエンジン関連のエラーコード
    public static class Engine
    {
        public const string ENGINE_NOT_AVAILABLE = "ENGINE_001";
        public const string ENGINE_STARTUP_FAILED = "ENGINE_002";
        public const string ENGINE_TIMEOUT = "ENGINE_003";
        public const string ENGINE_COMMUNICATION_ERROR = "ENGINE_004";
        public const string ENGINE_PROCESS_ERROR = "ENGINE_005";
    }

    // キャッシュ関連のエラーコード
    public static class Cache
    {
        public const string CACHE_READ_ERROR = "CACHE_001";
        public const string CACHE_WRITE_ERROR = "CACHE_002";
        public const string CACHE_CORRUPTED = "CACHE_003";
        public const string CACHE_FULL = "CACHE_004";
        public const string CACHE_PERMISSION_DENIED = "CACHE_005";
    }

    // 音声処理関連のエラーコード
    public static class Audio
    {
        public const string AUDIO_GENERATION_FAILED = "AUDIO_001";
        public const string AUDIO_PLAYBACK_FAILED = "AUDIO_002";
        public const string AUDIO_FORMAT_ERROR = "AUDIO_003";
        public const string AUDIO_DEVICE_ERROR = "AUDIO_004";
        public const string AUDIO_ENCODING_FAILED = "AUDIO_005";
        public const string MediaFoundationInitFailed = "AUDIO_006";
    }

    // API関連のエラーコード
    public static class Api
    {
        public const string API_REQUEST_FAILED = "API_001";
        public const string API_RESPONSE_INVALID = "API_002";
        public const string API_TIMEOUT = "API_003";
        public const string API_AUTHENTICATION_ERROR = "API_004";
        public const string API_RATE_LIMIT_EXCEEDED = "API_005";
    }

    // 一般的なエラーコード
    public static class General
    {
        public const string UNKNOWN_ERROR = "GEN_001";
        public const string INVALID_ARGUMENTS = "GEN_002";
        public const string OPERATION_CANCELLED = "GEN_003";
        public const string RESOURCE_NOT_FOUND = "GEN_004";
        public const string PERMISSION_DENIED = "GEN_005";
        public const string NETWORK_ERROR = "GEN_006";
        public const string TIMEOUT_ERROR = "GEN_007";
        public const string INVALID_OPERATION = "GEN_008";
    }
}

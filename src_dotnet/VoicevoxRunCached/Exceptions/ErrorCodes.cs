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
            Configuration.InvalidSettings => "設定が無効です",
            Configuration.MissingRequiredField => "必須フィールドが不足しています",
            Configuration.InvalidValue => "設定値が無効です",
            Configuration.FileNotFound => "設定ファイルが見つかりません",
            Configuration.ParseError => "設定ファイルの解析に失敗しました",

            // Engine
            Engine.EngineNotAvailable => "VOICEVOXエンジンが利用できません",
            Engine.EngineStartupFailed => "VOICEVOXエンジンの起動に失敗しました",
            Engine.EngineTimeout => "VOICEVOXエンジンの起動がタイムアウトしました",
            Engine.EngineCommunicationError => "VOICEVOXエンジンとの通信に失敗しました",
            Engine.EngineProcessError => "VOICEVOXエンジンプロセスでエラーが発生しました",

            // Cache
            Cache.CacheReadError => "キャッシュの読み込みに失敗しました",
            Cache.CacheWriteError => "キャッシュの書き込みに失敗しました",
            Cache.CacheCorrupted => "キャッシュが破損しています",
            Cache.CacheFull => "キャッシュが満杯です",
            Cache.CachePermissionDenied => "キャッシュへのアクセス権限がありません",

            // Audio
            Audio.AudioGenerationFailed => "音声の生成に失敗しました",
            Audio.AudioPlaybackFailed => "音声の再生に失敗しました",
            Audio.AudioFormatError => "音声フォーマットが無効です",
            Audio.AudioDeviceError => "音声デバイスでエラーが発生しました",
            Audio.AudioEncodingFailed => "音声のエンコードに失敗しました",
            Audio.MediaFoundationInitFailed => "MediaFoundationの初期化に失敗しました",

            // API
            Api.ApiRequestFailed => "APIリクエストに失敗しました",
            Api.ApiResponseInvalid => "APIレスポンスが無効です",
            Api.ApiTimeout => "APIリクエストがタイムアウトしました",
            Api.ApiAuthenticationError => "API認証に失敗しました",
            Api.ApiRateLimitExceeded => "APIレート制限を超過しました",

            // General
            General.UnknownError => "不明なエラーが発生しました",
            General.InvalidArguments => "無効な引数が指定されました",
            General.OperationCancelled => "操作がキャンセルされました",
            General.ResourceNotFound => "リソースが見つかりません",
            General.PermissionDenied => "アクセス権限がありません",
            General.NetworkError => "ネットワークエラーが発生しました",
            General.TimeoutError => "タイムアウトが発生しました",
            General.InvalidOperation => "無効な操作が試行されました",

            _ => "不明なエラーコードです"
        };
    }

    // 設定関連のエラーコード
    public static class Configuration
    {
        public const string InvalidSettings = "CONFIG_001";
        public const string MissingRequiredField = "CONFIG_002";
        public const string InvalidValue = "CONFIG_003";
        public const string FileNotFound = "CONFIG_004";
        public const string ParseError = "CONFIG_005";
    }

    // VOICEVOXエンジン関連のエラーコード
    public static class Engine
    {
        public const string EngineNotAvailable = "ENGINE_001";
        public const string EngineStartupFailed = "ENGINE_002";
        public const string EngineTimeout = "ENGINE_003";
        public const string EngineCommunicationError = "ENGINE_004";
        public const string EngineProcessError = "ENGINE_005";
    }

    // キャッシュ関連のエラーコード
    public static class Cache
    {
        public const string CacheReadError = "CACHE_001";
        public const string CacheWriteError = "CACHE_002";
        public const string CacheCorrupted = "CACHE_003";
        public const string CacheFull = "CACHE_004";
        public const string CachePermissionDenied = "CACHE_005";
    }

    // 音声処理関連のエラーコード
    public static class Audio
    {
        public const string AudioGenerationFailed = "AUDIO_001";
        public const string AudioPlaybackFailed = "AUDIO_002";
        public const string AudioFormatError = "AUDIO_003";
        public const string AudioDeviceError = "AUDIO_004";
        public const string AudioEncodingFailed = "AUDIO_005";
        public const string MediaFoundationInitFailed = "AUDIO_006";
    }

    // API関連のエラーコード
    public static class Api
    {
        public const string ApiRequestFailed = "API_001";
        public const string ApiResponseInvalid = "API_002";
        public const string ApiTimeout = "API_003";
        public const string ApiAuthenticationError = "API_004";
        public const string ApiRateLimitExceeded = "API_005";
    }

    // 一般的なエラーコード
    public static class General
    {
        public const string UnknownError = "GEN_001";
        public const string InvalidArguments = "GEN_002";
        public const string OperationCancelled = "GEN_003";
        public const string ResourceNotFound = "GEN_004";
        public const string PermissionDenied = "GEN_005";
        public const string NetworkError = "GEN_006";
        public const string TimeoutError = "GEN_007";
        public const string InvalidOperation = "GEN_008";
    }
}

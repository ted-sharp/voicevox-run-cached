namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// エラーコードの定義
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// Ctrl+C でキャンセルされたときの終了コード。シェルの SIGINT 終了の慣例 (128 + 2) に合わせ、
    /// 呼び出し側 (Hooks・スクリプト) が正常終了と区別できるようにする。
    /// </summary>
    public const int CancelledExitCode = 130;

    /// <summary>
    /// エラーコードに対応するプロセス終了コードを返す。カテゴリ (prefix) ごとに値を割り当てる。
    /// </summary>
    public static int GetExitCode(string? errorCode)
    {
        if (String.IsNullOrEmpty(errorCode))
            return 1;

        var prefix = errorCode.Split('_')[0];
        return prefix switch
        {
            "CONFIG" => 2,
            "ENGINE" => 3,
            "CACHE" => 4,
            "AUDIO" => 5,
            "API" => 6,
            _ => 1,
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

            // Engine
            Engine.EngineNotAvailable => "VOICEVOXエンジンが利用できません",
            Engine.EngineProcessError => "VOICEVOXエンジンプロセスでエラーが発生しました",

            // Cache
            Cache.CacheReadError => "キャッシュの読み込みに失敗しました",
            Cache.CachePermissionDenied => "キャッシュへのアクセス権限がありません",

            // Audio
            Audio.AudioGenerationFailed => "音声の生成に失敗しました",
            Audio.AudioFormatError => "音声フォーマットが無効です",
            Audio.AudioFileExportFailed => "音声ファイルの出力に失敗しました",

            // API
            Api.ApiRequestFailed => "APIリクエストに失敗しました",
            Api.ApiResponseInvalid => "APIレスポンスが無効です",
            Api.ApiTimeout => "APIリクエストがタイムアウトしました",
            Api.ApiAuthenticationError => "API認証に失敗しました",
            Api.ApiRateLimitExceeded => "APIレート制限を超過しました",

            // General
            General.UnknownError => "不明なエラーが発生しました",
            General.InvalidArguments => "無効な引数が指定されました",
            General.ResourceNotFound => "リソースが見つかりません",
            General.PermissionDenied => "アクセス権限がありません",

            _ => "不明なエラーコードです"
        };
    }

    // 設定関連のエラーコード
    public static class Configuration
    {
        public const string InvalidSettings = "CONFIG_001";
    }

    // VOICEVOXエンジン関連のエラーコード
    public static class Engine
    {
        public const string EngineNotAvailable = "ENGINE_001";
        public const string EngineProcessError = "ENGINE_005";
    }

    // キャッシュ関連のエラーコード
    public static class Cache
    {
        public const string CacheReadError = "CACHE_001";
        public const string CachePermissionDenied = "CACHE_005";
    }

    // 音声処理関連のエラーコード
    public static class Audio
    {
        public const string AudioGenerationFailed = "AUDIO_001";
        public const string AudioFormatError = "AUDIO_003";
        public const string AudioFileExportFailed = "AUDIO_007";
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
        public const string ResourceNotFound = "GEN_004";
        public const string PermissionDenied = "GEN_005";
    }
}

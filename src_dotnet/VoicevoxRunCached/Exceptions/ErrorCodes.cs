namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// エラーコードの定義
/// </summary>
/// <remarks>
/// 終了コード 2 は使わない。Claude Code の Hooks は終了コード 2 を「ブロッキングエラー」として扱い、
/// Stop フックなら Claude の停止を止めて作業を続けさせる。通知用に呼ばれる本ツールの失敗で
/// Claude の動作が変わらないよう、2 以外の値に割り当てる。
/// </remarks>
public static class ErrorCodes
{
    /// <summary>
    /// Ctrl+C でキャンセルされたときの終了コード。シェルの SIGINT 終了の慣例 (128 + 2) に合わせ、
    /// 呼び出し側 (Hooks・スクリプト) が正常終了と区別できるようにする。
    /// </summary>
    public const int CancelledExitCode = 130;

    /// <summary>
    /// 一部のセグメントだけ合成に失敗したときの終了コード (再生・保存は成功した分だけ行われる)。
    /// 全セグメントが失敗した場合は AUDIO エラー (5)。
    /// </summary>
    public const int PartialFailureExitCode = 8;

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
            "CONFIG" => 7,
            "ENGINE" => 3,
            "CACHE" => 4,
            "AUDIO" => 5,
            "API" => 6,
            // 他の処理が終わるのを待ちきれずに読み上げなかった。失敗とは区別できるよう専用の値にする
            "LOCK" => 9,
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

            // Lock
            Lock.LockWaitTimeout => "他の処理が終わるのを待ちきれませんでした",

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

    // プロセス間ロック関連のエラーコード
    public static class Lock
    {
        /// <summary>
        /// プロセス間ロックを上限時間 (<c>--max-wait</c> / <c>Lock.MaxWaitSeconds</c>) 内に取得できなかった。
        /// </summary>
        public const string LockWaitTimeout = "LOCK_001";
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

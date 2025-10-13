using System.Net;
using System.Text.Json;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// VoicevoxRunCachedアプリケーションの基本例外クラス
/// </summary>
public class VoicevoxRunCachedException : Exception
{
    public VoicevoxRunCachedException(string errorCode, string message, string userMessage, string? suggestedSolution = null, string? context = null)
        : base(message)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        SuggestedSolution = suggestedSolution;
        Context = context;
    }

    public VoicevoxRunCachedException(string errorCode, string message, string userMessage, Exception innerException, string? suggestedSolution = null, string? context = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        SuggestedSolution = suggestedSolution;
        Context = context;
    }

    /// <summary>
    /// エラーコード
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// ユーザーフレンドリーなエラーメッセージ
    /// </summary>
    public string UserMessage { get; }

    /// <summary>
    /// 推奨される解決策
    /// </summary>
    public string? SuggestedSolution { get; }

    /// <summary>
    /// エラーが発生したコンテキスト
    /// </summary>
    public string? Context { get; }

    /// <summary>
    /// エラーの詳細情報を取得
    /// </summary>
    public string GetDetailedErrorInfo()
    {
        var info = $"Error Code: {ErrorCode}\n";
        info += $"User Message: {UserMessage}\n";

        if (!String.IsNullOrEmpty(SuggestedSolution))
        {
            info += $"Suggested Solution: {SuggestedSolution}\n";
        }

        if (!String.IsNullOrEmpty(Context))
        {
            info += $"Context: {Context}\n";
        }

        info += $"Technical Details: {Message}";

        if (InnerException != null)
        {
            info += $"\nInner Exception: {InnerException.Message}";
        }

        return info;
    }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public string ToJson()
    {
        var data = new
        {
            ErrorCode,
            UserMessage,
            SuggestedSolution,
            Context,
            Message,
            InnerException = InnerException?.Message
        };

        return JsonSerializer.Serialize(data, JsonSerializerOptionsCache.Indented);
    }
}

/// <summary>
/// VoiceVox API関連の例外
/// </summary>
public class VoiceVoxApiException : VoicevoxRunCachedException
{
    public VoiceVoxApiException(string errorCode, string message, string userMessage, HttpStatusCode? statusCode = null, string? apiResponse = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "VoiceVox API")
    {
        StatusCode = statusCode;
        ApiResponse = apiResponse;
    }

    public VoiceVoxApiException(string errorCode, string message, string userMessage, Exception innerException, HttpStatusCode? statusCode = null, string? apiResponse = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "VoiceVox API")
    {
        StatusCode = statusCode;
        ApiResponse = apiResponse;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ApiResponse { get; }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode,
            UserMessage,
            SuggestedSolution,
            Context,
            Message,
            StatusCode,
            ApiResponse,
            InnerException = InnerException?.Message
        };

        return JsonSerializer.Serialize(data, JsonSerializerOptionsCache.Indented);
    }
}

/// <summary>
/// 設定関連の例外
/// </summary>
public class ConfigurationException : VoicevoxRunCachedException
{
    public ConfigurationException(string errorCode, string message, string userMessage, string? settingPath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Configuration")
    {
        SettingPath = settingPath;
    }

    public ConfigurationException(string errorCode, string message, string userMessage, Exception innerException, string? settingPath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Configuration")
    {
        SettingPath = settingPath;
    }

    public string? SettingPath { get; }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode,
            UserMessage,
            SuggestedSolution,
            Context,
            Message,
            SettingPath,
            InnerException = InnerException?.Message
        };

        return JsonSerializer.Serialize(data, JsonSerializerOptionsCache.Indented);
    }
}

/// <summary>
/// キャッシュ関連の例外
/// </summary>
public class CacheException : VoicevoxRunCachedException
{
    public CacheException(string errorCode, string message, string userMessage, string? cacheKey = null, string? cachePath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Cache")
    {
        CacheKey = cacheKey;
        CachePath = cachePath;
    }

    public CacheException(string errorCode, string message, string userMessage, Exception innerException, string? cacheKey = null, string? cachePath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Cache")
    {
        CacheKey = cacheKey;
        CachePath = cachePath;
    }

    public string? CacheKey { get; }
    public string? CachePath { get; }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode,
            UserMessage,
            SuggestedSolution,
            Context,
            Message,
            CacheKey,
            CachePath,
            InnerException = InnerException?.Message
        };

        return JsonSerializer.Serialize(data, JsonSerializerOptionsCache.Indented);
    }
}

/// <summary>
/// 音声処理関連の例外
/// </summary>
public class AudioProcessingException : VoicevoxRunCachedException
{
    public AudioProcessingException(string errorCode, string message, string userMessage, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Audio Processing")
    {
    }

    public AudioProcessingException(string errorCode, string message, string userMessage, Exception innerException, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Audio Processing")
    {
    }
}

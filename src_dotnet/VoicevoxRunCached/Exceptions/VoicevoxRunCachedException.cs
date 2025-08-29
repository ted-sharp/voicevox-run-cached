using System.Net;
using System.Text.Json;

namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// VoicevoxRunCachedアプリケーションの基本例外クラス
/// </summary>
[Serializable]
public class VoicevoxRunCachedException : Exception
{
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

    public VoicevoxRunCachedException(string errorCode, string message, string userMessage, string? suggestedSolution = null, string? context = null)
        : base(message)
    {
        this.ErrorCode = errorCode;
        this.UserMessage = userMessage;
        this.SuggestedSolution = suggestedSolution;
        this.Context = context;
    }

    public VoicevoxRunCachedException(string errorCode, string message, string userMessage, Exception innerException, string? suggestedSolution = null, string? context = null)
        : base(message, innerException)
    {
        this.ErrorCode = errorCode;
        this.UserMessage = userMessage;
        this.SuggestedSolution = suggestedSolution;
        this.Context = context;
    }

    /// <summary>
    /// エラーの詳細情報を取得
    /// </summary>
    public string GetDetailedErrorInfo()
    {
        var info = $"Error Code: {this.ErrorCode}\n";
        info += $"User Message: {this.UserMessage}\n";

        if (!String.IsNullOrEmpty(this.SuggestedSolution))
        {
            info += $"Suggested Solution: {this.SuggestedSolution}\n";
        }

        if (!String.IsNullOrEmpty(this.Context))
        {
            info += $"Context: {this.Context}\n";
        }

        info += $"Technical Details: {this.Message}";

        if (this.InnerException != null)
        {
            info += $"\nInner Exception: {this.InnerException.Message}";
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
            ErrorCode = this.ErrorCode,
            UserMessage = this.UserMessage,
            SuggestedSolution = this.SuggestedSolution,
            Context = this.Context,
            Message = this.Message,
            InnerException = this.InnerException?.Message
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// VoiceVox API関連の例外
/// </summary>
[Serializable]
public class VoiceVoxApiException : VoicevoxRunCachedException
{
    public HttpStatusCode? StatusCode { get; }
    public string? ApiResponse { get; }

    public VoiceVoxApiException(string errorCode, string message, string userMessage, HttpStatusCode? statusCode = null, string? apiResponse = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "VoiceVox API")
    {
        this.StatusCode = statusCode;
        this.ApiResponse = apiResponse;
    }

    public VoiceVoxApiException(string errorCode, string message, string userMessage, Exception innerException, HttpStatusCode? statusCode = null, string? apiResponse = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "VoiceVox API")
    {
        this.StatusCode = statusCode;
        this.ApiResponse = apiResponse;
    }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode = this.ErrorCode,
            UserMessage = this.UserMessage,
            SuggestedSolution = this.SuggestedSolution,
            Context = this.Context,
            Message = this.Message,
            StatusCode = this.StatusCode,
            ApiResponse = this.ApiResponse,
            InnerException = this.InnerException?.Message
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// 設定関連の例外
/// </summary>
[Serializable]
public class ConfigurationException : VoicevoxRunCachedException
{
    public string? SettingPath { get; }

    public ConfigurationException(string errorCode, string message, string userMessage, string? settingPath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Configuration")
    {
        this.SettingPath = settingPath;
    }

    public ConfigurationException(string errorCode, string message, string userMessage, Exception innerException, string? settingPath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Configuration")
    {
        this.SettingPath = settingPath;
    }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode = this.ErrorCode,
            UserMessage = this.UserMessage,
            SuggestedSolution = this.SuggestedSolution,
            Context = this.Context,
            Message = this.Message,
            SettingPath = this.SettingPath,
            InnerException = this.InnerException?.Message
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// キャッシュ関連の例外
/// </summary>
[Serializable]
public class CacheException : VoicevoxRunCachedException
{
    public string? CacheKey { get; }
    public string? CachePath { get; }

    public CacheException(string errorCode, string message, string userMessage, string? cacheKey = null, string? cachePath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Cache")
    {
        this.CacheKey = cacheKey;
        this.CachePath = cachePath;
    }

    public CacheException(string errorCode, string message, string userMessage, Exception innerException, string? cacheKey = null, string? cachePath = null, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Cache")
    {
        this.CacheKey = cacheKey;
        this.CachePath = cachePath;
    }

    /// <summary>
    /// 例外をJSON形式でシリアライズ
    /// </summary>
    public new string ToJson()
    {
        var data = new
        {
            ErrorCode = this.ErrorCode,
            UserMessage = this.UserMessage,
            SuggestedSolution = this.SuggestedSolution,
            Context = this.Context,
            Message = this.Message,
            CacheKey = this.CacheKey,
            CachePath = this.CachePath,
            InnerException = this.InnerException?.Message
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
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

using System.Net;

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

    public string ErrorCode { get; }
    public string UserMessage { get; }
    public string? SuggestedSolution { get; }
    public string? Context { get; }
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

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
/// VoiceVox API 関連の例外。catch (VoiceVoxApiException) で API 系エラーを横断的に拾うために残す。
/// </summary>
public class VoiceVoxApiException : VoicevoxRunCachedException
{
    public VoiceVoxApiException(string errorCode, string message, string userMessage, Exception innerException, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "VoiceVox API")
    {
    }
}

/// <summary>
/// 設定関連の例外
/// </summary>
public class ConfigurationException : VoicevoxRunCachedException
{
    public ConfigurationException(string errorCode, string message, string userMessage, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, suggestedSolution, "Configuration")
    {
    }
}

/// <summary>
/// キャッシュ関連の例外
/// </summary>
public class CacheException : VoicevoxRunCachedException
{
    public CacheException(string errorCode, string message, string userMessage, Exception innerException, string? suggestedSolution = null)
        : base(errorCode, message, userMessage, innerException, suggestedSolution, "Cache")
    {
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

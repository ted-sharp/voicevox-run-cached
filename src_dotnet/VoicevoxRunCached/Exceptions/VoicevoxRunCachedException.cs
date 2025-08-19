namespace VoicevoxRunCached.Exceptions;

/// <summary>
/// VoicevoxRunCachedアプリケーションの基底例外クラス
/// </summary>
public class VoicevoxRunCachedException : Exception
{
    public string ErrorCode { get; }

    public VoicevoxRunCachedException(string message, string errorCode = "UNKNOWN_ERROR")
        : base(message)
    {
        this.ErrorCode = errorCode;
    }

    public VoicevoxRunCachedException(string message, Exception innerException, string errorCode = "UNKNOWN_ERROR")
        : base(message, innerException)
    {
        this.ErrorCode = errorCode;
    }
}

/// <summary>
/// VOICEVOXエンジン関連の例外
/// </summary>
public class VoicevoxEngineException : VoicevoxRunCachedException
{
    public VoicevoxEngineException(string message, string errorCode = "ENGINE_ERROR")
        : base(message, errorCode)
    {
    }

    public VoicevoxEngineException(string message, Exception innerException, string errorCode = "ENGINE_ERROR")
        : base(message, innerException, errorCode)
    {
    }
}

/// <summary>
/// 設定関連の例外
/// </summary>
public class ConfigurationException : VoicevoxRunCachedException
{
    public ConfigurationException(string message, string errorCode = "CONFIG_ERROR")
        : base(message, errorCode)
    {
    }

    public ConfigurationException(string message, Exception innerException, string errorCode = "CONFIG_ERROR")
        : base(message, innerException, errorCode)
    {
    }
}

/// <summary>
/// キャッシュ関連の例外
/// </summary>
public class CacheException : VoicevoxRunCachedException
{
    public CacheException(string message, string errorCode = "CACHE_ERROR")
        : base(message, errorCode)
    {
    }

    public CacheException(string message, Exception innerException, string errorCode = "CACHE_ERROR")
        : base(message, innerException, errorCode)
    {
    }
}

/// <summary>
/// 音声処理関連の例外
/// </summary>
public class AudioProcessingException : VoicevoxRunCachedException
{
    public AudioProcessingException(string message, string errorCode = "AUDIO_ERROR")
        : base(message, errorCode)
    {
    }

    public AudioProcessingException(string message, Exception innerException, string errorCode = "AUDIO_ERROR")
        : base(message, innerException, errorCode)
    {
    }
}

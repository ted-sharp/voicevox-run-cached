using System.Net;
using System.Runtime.Serialization;

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

    private bool _disposed = false;

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

    protected VoicevoxRunCachedException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        this.ErrorCode = info.GetString(nameof(this.ErrorCode)) ?? "UNKNOWN_ERROR";
        this.UserMessage = info.GetString(nameof(this.UserMessage)) ?? "An error occurred";
        this.SuggestedSolution = info.GetString(nameof(this.SuggestedSolution));
        this.Context = info.GetString(nameof(this.Context));
    }

    [Obsolete("This method is obsolete. Use the new serialization pattern instead.")]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(this.ErrorCode), this.ErrorCode);
        info.AddValue(nameof(this.UserMessage), this.UserMessage);
        info.AddValue(nameof(this.SuggestedSolution), this.SuggestedSolution);
        info.AddValue(nameof(this.Context), this.Context);
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

    protected virtual void Dispose(bool disposing)
    {
        if (!this._disposed)
        {
            if (disposing)
            {
                // Dispose managed resources
                // 何もしない（基底クラスで既に処理済み）
            }
            else
            {
                // Finalizer called - dispose unmanaged resources only
                try
                {
                    // 非管理リソースの破棄（このクラスにはない）
                }
                catch
                {
                    // Ignore errors in finalizer
                }
            }
            this._disposed = true;
        }
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

    protected VoiceVoxApiException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        this.StatusCode = (HttpStatusCode?)info.GetValue(nameof(this.StatusCode), typeof(HttpStatusCode?));
        this.ApiResponse = info.GetString(nameof(this.ApiResponse));
    }

    [Obsolete("This method is obsolete. Use the new serialization pattern instead.")]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(this.StatusCode), this.StatusCode);
        info.AddValue(nameof(this.ApiResponse), this.ApiResponse);
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

    protected ConfigurationException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        this.SettingPath = info.GetString(nameof(this.SettingPath));
    }

    [Obsolete("This method is obsolete. Use the new serialization pattern instead.")]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(this.SettingPath), this.SettingPath);
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

    protected CacheException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        this.CacheKey = info.GetString(nameof(this.CacheKey));
        this.CachePath = info.GetString(nameof(this.CachePath));
    }

    [Obsolete("This method is obsolete. Use the new serialization pattern instead.")]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(this.CacheKey), this.CacheKey);
        info.AddValue(nameof(this.CachePath), this.CachePath);
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

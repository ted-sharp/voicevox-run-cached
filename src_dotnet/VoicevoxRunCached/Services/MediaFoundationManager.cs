using Microsoft.Extensions.Logging;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// MediaFoundation の Startup/Shutdown を参照カウンタ付きで管理する static サービス。
/// </summary>
public static class MediaFoundationManager
{
    private static readonly Lock _lock = new();
    private static int _referenceCount;
    private static bool _isInitialized;
    private static bool _processExitRegistered;

    /// <summary>
    /// 参照カウントを 1 加算し、未初期化なら MediaFoundationApi.Startup() を呼ぶ。
    /// </summary>
    public static void Acquire(ILogger? logger = null)
    {
        lock (_lock)
        {
            _referenceCount++;
            if (_isInitialized)
            {
                logger?.LogDebug("MediaFoundation は既に初期化済みです。参照カウント: {ReferenceCount}", _referenceCount);
                return;
            }

            try
            {
                MediaFoundationApi.Startup();
                _isInitialized = true;

                if (!_processExitRegistered)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdown();
                    _processExitRegistered = true;
                }

                logger?.LogDebug("MediaFoundation の初期化が完了しました。参照カウント: {ReferenceCount}", _referenceCount);
            }
            catch (Exception ex)
            {
                _referenceCount--;
                logger?.LogError(ex, "MediaFoundation の初期化に失敗しました");
                throw new VoicevoxRunCachedException(
                    ErrorCodes.Audio.MediaFoundationInitFailed,
                    "MediaFoundation initialization failed",
                    "音声システムの初期化に失敗しました",
                    "NAudioライブラリが正常にインストールされているか確認してください",
                    "MediaFoundation initialization"
                );
            }
        }
    }

    /// <summary>
    /// 参照カウントを増やさず、未初期化なら初期化のみ行う安全弁。
    /// (単発呼び出しの利用元向け)
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_isInitialized)
            {
                return;
            }

            MediaFoundationApi.Startup();
            _isInitialized = true;

            if (!_processExitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdown();
                _processExitRegistered = true;
            }
        }
    }

    /// <summary>
    /// 参照カウントを 1 減算し、0 になったら MediaFoundationApi.Shutdown() を呼ぶ。
    /// </summary>
    public static void Release(ILogger? logger = null)
    {
        lock (_lock)
        {
            if (_referenceCount <= 0)
            {
                logger?.LogDebug("MediaFoundation は既にシャットダウン済みです");
                return;
            }

            _referenceCount--;
            logger?.LogDebug("MediaFoundation の参照カウントを減らしました: {ReferenceCount}", _referenceCount);

            if (_referenceCount == 0 && _isInitialized)
            {
                SafeShutdown();
            }
        }
    }

    private static void SafeShutdown()
    {
        if (!_isInitialized)
        {
            return;
        }

        try
        {
            MediaFoundationApi.Shutdown();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to shutdown MediaFoundation");
        }
        finally
        {
            _isInitialized = false;
        }
    }
}

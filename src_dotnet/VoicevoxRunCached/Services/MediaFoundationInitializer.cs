using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// MediaFoundation初期化の専用サービス（シングルトンパターンで参照カウンタ付き）
/// </summary>
public class MediaFoundationInitializer
{
    private static readonly object _lock = new object();
    private static MediaFoundationInitializer? _instance;
    private static int _referenceCount;
    private static bool _isInitialized;

    private readonly ILogger? _logger;

    private MediaFoundationInitializer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// シングルトンインスタンスを取得します
    /// </summary>
    public static MediaFoundationInitializer GetInstance(ILogger? logger = null)
    {
        lock (_lock)
        {
            if (_instance == null)
            {
                _instance = new MediaFoundationInitializer(logger);
            }
            return _instance;
        }
    }

    /// <summary>
    /// MediaFoundationを初期化します（参照カウンタ付き）
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            _referenceCount++;

            if (_isInitialized)
            {
                _logger?.LogDebug("MediaFoundation は既に初期化済みです。参照カウント: {ReferenceCount}", _referenceCount);
                return;
            }

            try
            {
                MediaFoundationManager.Initialize();
                _isInitialized = true;
                _logger?.LogDebug("MediaFoundation の初期化が完了しました。参照カウント: {ReferenceCount}", _referenceCount);
            }
            catch (Exception ex)
            {
                _referenceCount--; // 初期化失敗時は参照カウントを戻す
                _logger?.LogError(ex, "MediaFoundation の初期化に失敗しました");
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
    /// 初期化済みであることを確認します
    /// </summary>
    public void EnsureInitialized()
    {
        lock (_lock)
        {
            if (!_isInitialized)
            {
                Initialize();
                return;
            }

            try
            {
                MediaFoundationManager.EnsureInitialized();
                _logger?.LogDebug("MediaFoundation の初期化状態を確認しました。参照カウント: {ReferenceCount}", _referenceCount);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MediaFoundation の初期化状態確認に失敗しました");
                throw new VoicevoxRunCachedException(
                    ErrorCodes.Audio.MediaFoundationInitFailed,
                    "MediaFoundation ensure initialized failed",
                    "音声システムの状態確認に失敗しました",
                    "アプリケーションを再起動してください",
                    "MediaFoundation initialization check"
                );
            }
        }
    }

    /// <summary>
    /// MediaFoundationをシャットダウンします（参照カウンタ付き）
    /// </summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_referenceCount <= 0)
            {
                _logger?.LogDebug("MediaFoundation は既にシャットダウン済みです");
                return;
            }

            _referenceCount--;
            _logger?.LogDebug("MediaFoundation の参照カウントを減らしました: {ReferenceCount}", _referenceCount);

            if (_referenceCount == 0 && _isInitialized)
            {
                try
                {
                    MediaFoundationManager.Shutdown();
                    _isInitialized = false;
                    _logger?.LogDebug("MediaFoundation のシャットダウンが完了しました");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "MediaFoundation のシャットダウン中にエラーが発生しました");
                    // シャットダウン時のエラーは例外をスローしない
                }
            }
        }
    }

    /// <summary>
    /// 現在の参照カウントを取得します（デバッグ用）
    /// </summary>
    public int GetReferenceCount()
    {
        lock (_lock)
        {
            return _referenceCount;
        }
    }

    /// <summary>
    /// 初期化状態を取得します（デバッグ用）
    /// </summary>
    public bool IsInitialized()
    {
        lock (_lock)
        {
            return _isInitialized;
        }
    }
}

using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// MediaFoundation初期化の専用サービス
/// </summary>
public class MediaFoundationInitializer
{
    private readonly ILogger? _logger;

    public MediaFoundationInitializer(ILogger? logger = null)
    {
        this._logger = logger;
    }

    /// <summary>
    /// MediaFoundationを初期化します
    /// </summary>
    public void Initialize()
    {
        try
        {
            MediaFoundationManager.Initialize();
            this._logger?.LogDebug("MediaFoundation の初期化が完了しました");
        }
        catch (Exception ex)
        {
            this._logger?.LogError(ex, "MediaFoundation の初期化に失敗しました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.Audio.MediaFoundationInitFailed,
                "MediaFoundation initialization failed",
                "音声システムの初期化に失敗しました",
                "NAudioライブラリが正常にインストールされているか確認してください",
                "MediaFoundation initialization"
            );
        }
    }

    /// <summary>
    /// 初期化済みであることを確認します
    /// </summary>
    public void EnsureInitialized()
    {
        try
        {
            MediaFoundationManager.EnsureInitialized();
            this._logger?.LogDebug("MediaFoundation の初期化状態を確認しました");
        }
        catch (Exception ex)
        {
            this._logger?.LogError(ex, "MediaFoundation の初期化状態確認に失敗しました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.Audio.MediaFoundationInitFailed,
                "MediaFoundation ensure initialized failed",
                "音声システムの状態確認に失敗しました",
                "アプリケーションを再起動してください",
                "MediaFoundation initialization check"
            );
        }
    }

    /// <summary>
    /// MediaFoundationをシャットダウンします（通常はアプリケーション終了時）
    /// </summary>
    public void Shutdown()
    {
        try
        {
            MediaFoundationManager.Shutdown();
            this._logger?.LogDebug("MediaFoundation のシャットダウンが完了しました");
        }
        catch (Exception ex)
        {
            this._logger?.LogWarning(ex, "MediaFoundation のシャットダウン中にエラーが発生しました");
            // シャットダウン時のエラーは例外をスローしない
        }
    }
}
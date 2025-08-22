using Microsoft.Extensions.Logging;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// キャンセレーション処理を統一管理するクラス
/// </summary>
public class CancellationManager : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private bool _disposed;

    public CancellationManager(Microsoft.Extensions.Logging.ILogger logger)
    {
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this._cancellationTokenSource = new CancellationTokenSource();
        this.SetupCancellationHandling();
    }

    /// <summary>
    /// キャンセレーショントークンを取得します
    /// </summary>
    public CancellationToken Token => this._cancellationTokenSource.Token;

    /// <summary>
    /// キャンセレーション処理を設定します
    /// </summary>
    private void SetupCancellationHandling()
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            this._logger.LogWarning("Cancellation requested (Ctrl+C). Attempting graceful shutdown...");
            Log.Warning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します...");
            this._cancellationTokenSource.Cancel();
        };
    }

    /// <summary>
    /// キャンセレーションを要求します
    /// </summary>
    public void Cancel()
    {
        this._cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// キャンセレーションが要求されているかどうかを確認します
    /// </summary>
    public bool IsCancellationRequested => this._cancellationTokenSource.IsCancellationRequested;

    /// <summary>
    /// リソースを破棄します
    /// </summary>
    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this._cancellationTokenSource?.Dispose();
                this._disposed = true;
            }
            catch (Exception ex)
            {
                this._logger?.LogError(ex, "CancellationManagerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}

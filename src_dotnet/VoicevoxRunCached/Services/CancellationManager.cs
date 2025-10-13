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
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cancellationTokenSource = new CancellationTokenSource();
        SetupCancellationHandling();
    }

    /// <summary>
    /// キャンセレーショントークンを取得します
    /// </summary>
    public CancellationToken Token => _cancellationTokenSource.Token;

    /// <summary>
    /// キャンセレーションが要求されているかどうかを確認します
    /// </summary>
    public bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

    /// <summary>
    /// リソースを破棄します
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    _cancellationTokenSource.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CancellationManagerの破棄中にエラーが発生しました");
                }
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// キャンセレーション処理を設定します
    /// </summary>
    private void SetupCancellationHandling()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _logger.LogWarning("Cancellation requested (Ctrl+C). Attempting graceful shutdown...");
            Log.Warning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します...");
            _cancellationTokenSource.Cancel();
        };
    }

    /// <summary>
    /// キャンセレーションを要求します
    /// </summary>
    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }
}

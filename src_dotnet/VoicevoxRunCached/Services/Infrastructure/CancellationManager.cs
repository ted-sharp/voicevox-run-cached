using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// Ctrl+C を協調キャンセルに変換する。1 回目はキャンセルを要求してプロセスの終了を止め (後片付けを行わせる)、
/// 2 回目以降は既定の動作 (即時の強制終了) に任せる。
/// </summary>
public class CancellationManager : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private bool _disposed;

    public CancellationManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>
    /// キャンセレーショントークンを取得します
    /// </summary>
    public CancellationToken Token => _cancellationTokenSource.Token;

    public void Dispose()
    {
        // ハンドラを残すと、破棄後の Ctrl+C で破棄済みの CTS を Cancel して ObjectDisposedException になる。
        // 外す直前に呼ばれたハンドラが実行中の可能性があるので、CTS の破棄はハンドラと同じ lock の中で行う
        Console.CancelKeyPress -= OnCancelKeyPress;
        lock (_lock)
        {
            if (_disposed)
                return;
            _cancellationTokenSource.Dispose();
            _disposed = true;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = TryRequestCancellation();
    }

    /// <summary>
    /// Ctrl+C 1 回分の処理。
    /// </summary>
    /// <returns>
    /// true: キャンセルを要求した (プロセスを終了させない)。
    /// false: 2 回目以降、または破棄後。既定の動作 (即時の強制終了) に任せる。終了処理が終わらないときの逃げ道
    /// </returns>
    public bool TryRequestCancellation()
    {
        lock (_lock)
        {
            if (_disposed)
                return false;

            if (_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogWarning("再度のキャンセル要求 (Ctrl+C)。強制終了します");
                return false;
            }

            _logger.LogWarning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します (もう一度押すと強制終了)");
            // Cancel() は登録済みコールバックと、その先の await の続きをこのスレッドで同期実行することがある。
            // lock を持ったまま後片付けまで走ると 2 回目の Ctrl+C がここで待たされ強制終了できないので、
            // 状態だけ同期で切り替えてコールバックはスレッドプールで実行させる
            _ = _cancellationTokenSource.CancelAsync();
            return true;
        }
    }
}

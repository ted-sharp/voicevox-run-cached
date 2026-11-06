using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// キャンセレーション処理を統一管理するクラス
/// </summary>
public class CancellationManager : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ILogger _logger;
    private bool _disposed;

    public CancellationManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>
    /// キャンセレーショントークンを取得します
    /// </summary>
    public CancellationToken Token => _cancellationTokenSource.Token;

    /// <summary>
    /// キャンセレーションが要求されているかどうかを確認します
    /// </summary>
    public bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

    public void Dispose()
    {
        if (_disposed)
            return;
        // ハンドラを残すと、破棄後の Ctrl+C で破棄済みの CTS を Cancel して ObjectDisposedException になる
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cancellationTokenSource.Dispose();
        _disposed = true;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _logger.LogWarning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します...");
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// キャンセレーションを要求します
    /// </summary>
    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }
}

using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 現在の行に回転アニメーションとメッセージを表示する。
/// </summary>
/// <remarks>
/// 描画は <c>\r</c> で現在行の先頭に戻って書き直すだけで、カーソル位置を保存して戻ることはしない。
/// 以前は開始時の位置へ戻って上書きしていたため、表示中に出たログ (エンジン起動・キャッシュクリアの info、
/// フィラー生成失敗の Warning など) がスピナーに消されていた。
/// 描画後はカーソルを行頭に置くので、表示中に出たログは行頭から書かれ、スピナーはその次の行に移って描画を続ける。
/// 描画先はログと同じ stderr (stdout はコマンドの結果だけにする)。stderr がリダイレクトされているとき (Hooks・パイプ) は何も描画しない。
/// </remarks>
public class ProgressSpinner : IDisposable
{
    // カーソル位置から行末までを消す ANSI エスケープ (前回の長いメッセージの残りを消す)
    private const string ClearToEndOfLine = "\e[K";

    private readonly Task _animationTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly string[] _frames = ["|", "/", "-", "\\"];

    private readonly Lock _lock = new();
    private bool _isDisposed;
    private string _message;

    public ProgressSpinner(string message = "")
    {
        _message = message;
        _animationTask = Console.IsErrorRedirected ? Task.CompletedTask : Task.Run(AnimateAsync);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _cancellationTokenSource.Cancel();
        _animationTask.Wait(AudioConstants.ProgressSpinnerStopWaitTimeoutMs);
        _cancellationTokenSource.Dispose();
        _isDisposed = true;
    }

    public void UpdateMessage(string message)
    {
        lock (_lock)
        {
            _message = message;
        }
    }

    private async Task AnimateAsync()
    {
        int frameIndex = 0;
        Console.CursorVisible = false;

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                string currentMessage;
                lock (_lock)
                {
                    currentMessage = _message;
                }

                // 1 回の Write で書く (Console.Error は同期化されているので、他スレッドのログと途中で混ざらない)
                Console.Error.Write($"\r\e[33m{_frames[frameIndex]}\e[0m {currentMessage}{ClearToEndOfLine}\r");
                frameIndex = (frameIndex + 1) % _frames.Length;

                await Task.Delay(100, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose による停止
        }
        finally
        {
            Console.Error.Write($"\r{ClearToEndOfLine}");
            Console.CursorVisible = true;
        }
    }
}

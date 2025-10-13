namespace VoicevoxRunCached.Services;

public class ProgressSpinner : IDisposable
{
    private readonly Task _animationTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly string[] _frames = ["|", "/", "-", "\\"];

    // Use .NET 9 C# 13 System.Threading.Lock for better lock semantics/perf
    private readonly Lock _lock = new();
    private bool _isDisposed = false;
    private string _message = "";

    public ProgressSpinner(string message = "")
    {
        _message = message;
        _animationTask = Task.Run(AnimateAsync);
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _cancellationTokenSource.Cancel();
            try
            {
                _animationTask.Wait(1000); // Wait up to 1 second for cleanup
            }
            catch (AggregateException)
            {
                // Ignore cleanup timeout
            }
            _cancellationTokenSource.Dispose();
            _isDisposed = true;
        }
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
        var originalCursorLeft = Console.CursorLeft;
        var originalCursorTop = Console.CursorTop;

        try
        {
            Console.CursorVisible = false;

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                string currentMessage;
                lock (_lock)
                {
                    currentMessage = _message;
                }

                Console.SetCursorPosition(originalCursorLeft, originalCursorTop);
                Console.Write($"\e[33m{_frames[frameIndex]}\e[0m {currentMessage}");

                // Clear any remaining characters from previous longer messages
                var currentLength = _frames[frameIndex].Length + 1 + currentMessage.Length;
                var consoleWidth = Console.WindowWidth;
                if (currentLength < consoleWidth)
                {
                    Console.Write(new string(' ', Math.Min(20, consoleWidth - currentLength - 1)));
                }

                frameIndex = (frameIndex + 1) % _frames.Length;

                await Task.Delay(100, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        finally
        {
            // Clear the spinner line
            Console.SetCursorPosition(originalCursorLeft, originalCursorTop);
            Console.Write(new string(' ', Math.Min(Console.WindowWidth - 1, 80)));
            Console.SetCursorPosition(originalCursorLeft, originalCursorTop);
            Console.CursorVisible = true;
        }
    }
}

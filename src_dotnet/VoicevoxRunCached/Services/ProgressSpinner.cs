using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoicevoxRunCached.Services;

public class ProgressSpinner : IDisposable
{
    private readonly string[] _frames = ["|", "/", "-", "\\"];
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _animationTask;
    private readonly object _lock = new();
    private string _message = "";
    private bool _isDisposed = false;

    public ProgressSpinner(string message = "")
    {
        _message = message;
        _animationTask = Task.Run(AnimateAsync);
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
}
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
        this._message = message;
        this._animationTask = Task.Run(this.AnimateAsync);
    }

    public void UpdateMessage(string message)
    {
        lock (this._lock)
        {
            this._message = message;
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

            while (!this._cancellationTokenSource.Token.IsCancellationRequested)
            {
                string currentMessage;
                lock (this._lock)
                {
                    currentMessage = this._message;
                }

                Console.SetCursorPosition(originalCursorLeft, originalCursorTop);
                Console.Write($"\e[33m{this._frames[frameIndex]}\e[0m {currentMessage}");

                // Clear any remaining characters from previous longer messages
                var currentLength = this._frames[frameIndex].Length + 1 + currentMessage.Length;
                var consoleWidth = Console.WindowWidth;
                if (currentLength < consoleWidth)
                {
                    Console.Write(new string(' ', Math.Min(20, consoleWidth - currentLength - 1)));
                }

                frameIndex = (frameIndex + 1) % this._frames.Length;

                await Task.Delay(100, this._cancellationTokenSource.Token);
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
        if (!this._isDisposed)
        {
            this._cancellationTokenSource.Cancel();
            try
            {
                this._animationTask.Wait(1000); // Wait up to 1 second for cleanup
            }
            catch (AggregateException)
            {
                // Ignore cleanup timeout
            }
            this._cancellationTokenSource.Dispose();
            this._isDisposed = true;
        }
    }
}

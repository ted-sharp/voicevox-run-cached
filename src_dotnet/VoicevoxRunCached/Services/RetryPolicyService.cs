using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Serilog;

namespace VoicevoxRunCached.Services;

public class RetryPolicyService
{
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
    private readonly AsyncPolicy _combinedPolicy;

    // 新しいプロパティ
    private readonly int _maxRetryAttempts = 3;
    private readonly int _baseDelayMs = 1000; // 1秒
    private readonly int _maxDelayMs = 30000; // 30秒

    public RetryPolicyService()
    {
        // 指数バックオフ付きリトライポリシー
        this._retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .Or<OperationCanceledException>()
            .WaitAndRetryAsync(
                retryCount: this._maxRetryAttempts,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)), // 指数バックオフ: 1s, 2s, 4s
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    Log.Warning(exception, "リトライ {RetryCount}/{MaxRetries} - {TimeSpan}後に再試行します", retryCount, this._maxRetryAttempts, timeSpan);
                }
            );

        // サーキットブレーカーポリシー
        this._circuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, duration) =>
                {
                    Log.Error(exception, "サーキットブレーカーが開きました - {Duration}間隔でブロック", duration);
                },
                onReset: () =>
                {
                    Log.Information("サーキットブレーカーがリセットされました");
                },
                onHalfOpen: () =>
                {
                    Log.Information("サーキットブレーカーが半開状態になりました");
                }
            );

        // タイムアウトポリシー
        var timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromSeconds(30));

        // ポリシーを組み合わせ
        this._combinedPolicy = Policy.WrapAsync(this._retryPolicy, this._circuitBreakerPolicy, timeoutPolicy);
    }

    public AsyncPolicy GetRetryPolicy() => this._retryPolicy;
    public AsyncPolicy GetCircuitBreakerPolicy() => this._circuitBreakerPolicy;
    public AsyncPolicy GetCombinedPolicy() => this._combinedPolicy;

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default)
    {
        var retryCount = 0;
        var lastException = default(Exception);

        while (retryCount < this._maxRetryAttempts)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (this.ShouldRetryHttpError(ex))
            {
                lastException = ex;
                retryCount++;

                if (retryCount < this._maxRetryAttempts)
                {
                    var delay = this.CalculateDelay(retryCount, ex.StatusCode);
                    Log.Warning(ex, "HTTP error during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
                        operationName, delay, retryCount, this._maxRetryAttempts);

                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"Operation '{operationName}' was cancelled", null, cancellationToken);
            }
            catch (TaskCanceledException ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount < this._maxRetryAttempts)
                {
                    var delay = this.CalculateDelay(retryCount, null);
                    Log.Warning(ex, "Timeout during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
                        operationName, delay, retryCount, this._maxRetryAttempts);

                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (Exception ex) when (this.ShouldRetryException(ex))
            {
                lastException = ex;
                retryCount++;

                if (retryCount < this._maxRetryAttempts)
                {
                    var delay = this.CalculateDelay(retryCount, null);
                    Log.Warning(ex, "Retryable error during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
                        operationName, delay, retryCount, this._maxRetryAttempts);

                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Non-retryable error
                Log.Error(ex, "Non-retryable error during {OperationName}", operationName);
                throw;
            }
        }

        // All retries exhausted
        var errorMessage = $"Operation '{operationName}' failed after {this._maxRetryAttempts} attempts";
        if (lastException != null)
        {
            throw new InvalidOperationException(errorMessage, lastException);
        }

        throw new InvalidOperationException(errorMessage);
    }

    private bool ShouldRetryHttpError(HttpRequestException ex)
    {
        if (!ex.StatusCode.HasValue)
            return false;

        // Retry on server errors and rate limiting
        return ex.StatusCode.Value switch
        {
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            _ => false
        };
    }

    private bool ShouldRetryException(Exception ex)
    {
        // Retry on network-related exceptions
        return ex is IOException ||
               ex is SocketException ||
               ex is WebException ||
               ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private int CalculateDelay(int retryCount, HttpStatusCode? statusCode)
    {
        var baseDelay = this._baseDelayMs;

        // Apply exponential backoff
        var exponentialDelay = baseDelay * Math.Pow(2, retryCount - 1);

        // Add jitter to prevent thundering herd
        var jitter = Random.Shared.Next(-100, 100);

        // Special handling for rate limiting
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            exponentialDelay = Math.Max(exponentialDelay, 1000); // Minimum 1 second for rate limiting
        }

        var finalDelay = (int)Math.Min(exponentialDelay + jitter, this._maxDelayMs);
        return Math.Max(finalDelay, 100); // Minimum 100ms
    }
}

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services;

public class RetryPolicyService
{
    private const int BaseDelayMs = 1000;
    private const int MaxDelayMs = 30000;
    private const int MaxRetryAttempts = 3;

    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
    private readonly AsyncPolicy _combinedPolicy;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly ILogger _logger;

    public RetryPolicyService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .Or<OperationCanceledException>()
            .WaitAndRetryAsync(
                retryCount: MaxRetryAttempts,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
                onRetry: (exception, timeSpan, retryCount, _) =>
                {
                    _logger.LogWarning(exception, "リトライ {RetryCount}/{MaxRetries} - {TimeSpan}後に再試行します",
                        retryCount, MaxRetryAttempts, timeSpan);
                }
            );

        _circuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: AudioConstants.CircuitBreakerExceptionThreshold,
                durationOfBreak: TimeSpan.FromSeconds(AudioConstants.CircuitBreakerDurationSeconds),
                onBreak: (exception, duration) =>
                    _logger.LogError(exception, "サーキットブレーカーが開きました - {Duration}間隔でブロック", duration),
                onReset: () => _logger.LogInformation("サーキットブレーカーがリセットされました"),
                onHalfOpen: () => _logger.LogInformation("サーキットブレーカーが半開状態になりました")
            );

        var timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromSeconds(AudioConstants.OperationTimeoutSeconds));
        _combinedPolicy = Policy.WrapAsync(_retryPolicy, _circuitBreakerPolicy, timeoutPolicy);
    }

    public AsyncPolicy GetRetryPolicy() => _retryPolicy;
    public AsyncPolicy GetCircuitBreakerPolicy() => _circuitBreakerPolicy;
    public AsyncPolicy GetCombinedPolicy() => _combinedPolicy;

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default)
    {
        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount < MaxRetryAttempts)
        {
            try
            {
                return await operation();
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"Operation '{operationName}' was cancelled", null, cancellationToken);
            }
            catch (HttpRequestException ex) when (ShouldRetryHttpError(ex))
            {
                lastException = ex;
                retryCount++;
                await HandleRetryableErrorAsync(ex, operationName, retryCount, ex.StatusCode, cancellationToken);
            }
            catch (TaskCanceledException ex)
            {
                lastException = ex;
                retryCount++;
                await HandleRetryableErrorAsync(ex, operationName, retryCount, null, cancellationToken);
            }
            catch (Exception ex) when (ShouldRetryException(ex))
            {
                lastException = ex;
                retryCount++;
                await HandleRetryableErrorAsync(ex, operationName, retryCount, null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Non-retryable error during {OperationName}", operationName);
                throw;
            }
        }

        throw new InvalidOperationException($"Operation '{operationName}' failed after maximum retry attempts", lastException);
    }

    private async Task HandleRetryableErrorAsync(
        Exception exception,
        string operationName,
        int retryCount,
        HttpStatusCode? statusCode,
        CancellationToken cancellationToken)
    {
        if (retryCount >= MaxRetryAttempts)
        {
            return;
        }

        var delay = CalculateDelay(retryCount, statusCode);
        var errorType = GetErrorType(exception);

        _logger.LogWarning(exception, "{ErrorType} during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
            errorType, operationName, delay, retryCount, MaxRetryAttempts);

        await Task.Delay(delay, cancellationToken);
    }

    private static string GetErrorType(Exception exception) => exception switch
    {
        HttpRequestException => "HTTP error",
        TaskCanceledException => "Timeout",
        _ => "Retryable error"
    };

    private static bool ShouldRetryHttpError(HttpRequestException ex)
    {
        if (!ex.StatusCode.HasValue)
            return false;

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

    private static bool ShouldRetryException(Exception ex)
    {
        return ex is IOException
            || ex is SocketException
            || ex is WebException
            || ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateDelay(int retryCount, HttpStatusCode? statusCode)
    {
        var exponentialDelay = BaseDelayMs * Math.Pow(2, retryCount - 1);
        var jitter = Random.Shared.Next(-100, 100);

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            exponentialDelay = Math.Max(exponentialDelay, 1000);
        }

        var finalDelay = (int)Math.Min(exponentialDelay + jitter, MaxDelayMs);
        return Math.Max(finalDelay, 100);
    }
}

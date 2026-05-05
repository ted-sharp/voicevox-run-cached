using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// HTTP/IO 系の retry ポリシーを集約する。指数バックオフ + jitter で再試行する。
/// </summary>
/// <remarks>
/// HTTP ステータスコード別の判定 (5xx/429 のみ retry) と TooManyRequests 下限調整を持つため、
/// 単純な Polly ポリシーで置き換えるとロジックが薄くなる。手書きループで保守する方針。
/// </remarks>
public class RetryPolicyService
{
    private const int BaseDelayMs = AudioConstants.RetryBaseDelayMs;
    private const int MaxDelayMs = AudioConstants.RetryMaxDelayMs;
    private const int MaxRetryAttempts = AudioConstants.RetryMaxAttempts;

    private readonly ILogger _logger;

    public RetryPolicyService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

        // リトライ自体は正常フロー (5xx/429/timeout に対する既定の挙動) なので Debug。
        // 全試行が失敗した時の `Non-retryable error` / `failed after maximum retry attempts` は
        // 別のログパスで Error として残るため、運用ログ上の追跡性は保たれる。
        _logger.LogDebug(exception, "{ErrorType} during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
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

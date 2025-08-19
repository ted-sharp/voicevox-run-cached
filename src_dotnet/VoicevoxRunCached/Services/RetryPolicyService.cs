using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Serilog;

namespace VoicevoxRunCached.Services;

public class RetryPolicyService
{
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
    private readonly AsyncPolicy _combinedPolicy;

    public RetryPolicyService()
    {
        // 指数バックオフ付きリトライポリシー
        this._retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutException>()
            .Or<OperationCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)), // 指数バックオフ: 1s, 2s, 4s
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    Log.Warning(exception, "リトライ {RetryCount}/3 - {TimeSpan}後に再試行します", retryCount, timeSpan);
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

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, string operationName = "操作")
    {
        try
        {
            Log.Debug("{Operation} を実行中...", operationName);
            var result = await this._combinedPolicy.ExecuteAsync(action);
            Log.Debug("{Operation} が正常に完了しました", operationName);
            return result;
        }
        catch (BrokenCircuitException ex)
        {
            Log.Error(ex, "{Operation} がサーキットブレーカーによりブロックされました", operationName);
            throw;
        }
        catch (TimeoutRejectedException ex)
        {
            Log.Error(ex, "{Operation} がタイムアウトしました", operationName);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation} が失敗しました", operationName);
            throw;
        }
    }

    public async Task ExecuteWithRetryAsync(Func<Task> action, string operationName = "操作")
    {
        await this.ExecuteWithRetryAsync(async () =>
        {
            await action();
            return true;
        }, operationName);
    }
}

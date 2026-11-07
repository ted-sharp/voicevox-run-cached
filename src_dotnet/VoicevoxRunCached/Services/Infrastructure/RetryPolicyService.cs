using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// HTTP/IO 系の retry ポリシーを集約する。指数バックオフ + jitter で再試行する。
/// </summary>
/// <remarks>
/// HTTP ステータスコード別の判定 (502/503/504/429 のみ retry) と TooManyRequests 下限調整を持つため、
/// 単純な Polly ポリシーで置き換えるとロジックが薄くなる。手書きループで保守する方針。
/// </remarks>
public class RetryPolicyService
{
    private readonly int _baseDelayMs;
    private readonly int _maxDelayMs;
    private readonly int _maxRetryAttempts;
    private readonly ILogger _logger;

    /// <param name="baseDelayMs">指数バックオフの初回遅延 (ms)。テストで短縮可能</param>
    /// <param name="maxDelayMs">バックオフ上限 (ms)</param>
    /// <param name="maxRetryAttempts">最大試行回数</param>
    public RetryPolicyService(
        ILogger logger,
        int baseDelayMs = AudioConstants.RetryBaseDelayMs,
        int maxDelayMs = AudioConstants.RetryMaxDelayMs,
        int maxRetryAttempts = AudioConstants.RetryMaxAttempts)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseDelayMs = baseDelayMs;
        _maxDelayMs = maxDelayMs;
        _maxRetryAttempts = maxRetryAttempts;
    }

    /// <remarks>
    /// リトライ対象外の例外と、最後の試行で起きた例外は元の型のまま呼び出し元へ投げる
    /// (包み直さない)。呼び出し元 (<c>VoiceVoxApiClient.MapToApiException</c>) が HTTP ステータス等から
    /// ErrorCode を決めるため、包むと 500/503 (ENGINE) や 429・タイムアウト (API) の終了コードが
    /// 「不明なエラー」になってしまう。Error ログも呼び出し元が書く。
    /// </remarks>
    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"Operation '{operationName}' was cancelled", null, cancellationToken);
            }
            catch (Exception ex) when (attempt < _maxRetryAttempts && IsRetryable(ex))
            {
                await DelayBeforeRetryAsync(ex, operationName, attempt, cancellationToken);
            }
        }
    }

    private async Task DelayBeforeRetryAsync(
        Exception exception,
        string operationName,
        int attempt,
        CancellationToken cancellationToken)
    {
        var statusCode = (exception as HttpRequestException)?.StatusCode;
        var delay = CalculateDelay(attempt, statusCode);
        var errorType = GetErrorType(exception);

        // リトライ自体は正常フロー (502/503/504/429 等に対する既定の挙動) なので Debug。
        // 最終的な失敗は呼び出し元が Error として残す。
        _logger.LogDebug(exception, "{ErrorType} during {OperationName}, retrying in {Delay}ms (attempt {RetryCount}/{MaxRetries})",
            errorType, operationName, delay, attempt, _maxRetryAttempts);

        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// 502/503/504/429 の HTTP エラーと、一時的な I/O・ソケットエラーをリトライ対象とする。
    /// </summary>
    /// <remarks>
    /// <para>HttpClient のタイムアウト (<see cref="TaskCanceledException"/>) はリトライしない。タイムアウト (既定 30 秒) を
    /// 待ってもエンジンが応答しないなら、もう一度待っても応答しないことがほとんどで、リトライすると 1 件で約 93 秒
    /// (30 秒 × 3 回 + バックオフ) プロセス間ロックを握り、後続の読み上げや Hooks を待たせるため。</para>
    /// <para>500 もリトライしない。VOICEVOX の 500 は特定の文が合成できないときに返るもので、同じ文を送り直しても同じ結果になる
    /// (バックオフの分だけ遅れるだけ)。</para>
    /// </remarks>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        HttpRequestException httpEx => ShouldRetryHttpError(httpEx),
        IOException or SocketException or WebException => true,
        _ => false,
    };

    private static string GetErrorType(Exception exception) => exception switch
    {
        HttpRequestException => "HTTP error",
        _ => "Retryable error"
    };

    private static bool ShouldRetryHttpError(HttpRequestException ex)
    {
        if (!ex.StatusCode.HasValue)
            return false;

        return ex.StatusCode.Value switch
        {
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            _ => false
        };
    }

    private int CalculateDelay(int retryCount, HttpStatusCode? statusCode)
    {
        var exponentialDelay = _baseDelayMs * Math.Pow(2, retryCount - 1);
        var jitter = Random.Shared.Next(-100, 100);

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            exponentialDelay = Math.Max(exponentialDelay, 1000);
        }

        var finalDelay = (int)Math.Min(exponentialDelay + jitter, _maxDelayMs);
        // テスト用に短い baseDelay (例: 1) を渡された場合の最低保証は外す。
        // 本番デフォルトの 1000ms なら下限はもう超えているので影響無し。
        return Math.Max(finalDelay, 0);
    }
}

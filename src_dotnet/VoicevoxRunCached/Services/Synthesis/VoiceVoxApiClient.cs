using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

public class VoiceVoxApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly RetryPolicyService _retryPolicyService;
    private readonly VoiceVoxSettings _settings;
    private readonly ILogger _logger;

    public VoiceVoxApiClient(VoiceVoxSettings settings, RetryPolicyService retryPolicyService, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _retryPolicyService = retryPolicyService ?? throw new ArgumentNullException(nameof(retryPolicyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settings.BaseUrl),
            // タイムアウト (ConnectionTimeout) は SendAsync でリクエストごとに掛ける。
            // /initialize_speaker だけはモデルの読み込みが終わるまで待つため
            Timeout = Timeout.InfiniteTimeSpan
        };

        _logger.LogDebug("VoiceVoxApiClient を初期化しました - BaseUrl: {BaseUrl}, Timeout: {Timeout}s",
            _settings.BaseUrl, _settings.ConnectionTimeout);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public Task<List<Speaker>> GetSpeakersAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteApiAsync("get speakers", async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.BaseUrl}/speakers");
            using var response = await SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<Speaker>>(content, JsonOptions) ?? new();
        }, cancellationToken);
    }

    public Task InitializeSpeakerAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("スピーカー {SpeakerId} を初期化中...", speakerId);
        return ExecuteApiAsync($"initialize speaker {speakerId}", async ct =>
        {
            // skip_reinit=true: 読み込み済みなら即座に返る (付けないと毎回初期化し直し、1 回 1.7 秒ほどかかる)
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.BaseUrl}/initialize_speaker?speaker={speakerId}&skip_reinit=true");
            // エンジンはモデルの読み込みが終わってから応答する (応答 = 読み込み完了)。かかる時間はマシンとモデル次第なので
            // ConnectionTimeout で打ち切らない (打ち切ると、エンジン起動直後の最初の読み上げが読み込み中にタイムアウトで失敗する)。
            // エンジンが落ちれば接続エラーですぐ失敗する。固まった場合は Ctrl+C か呼び出し元 (Hooks) のタイムアウトで止まる
            using var response = await SendAsync(request, ct, applyTimeout: false);
            _logger.LogInformation("スピーカー {SpeakerId} の初期化が完了しました", speakerId);
            return true;
        }, cancellationToken);
    }

    public Task<string> GenerateAudioQueryAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteApiAsync("generate audio query", async ct =>
        {
            _logger.LogDebug("Audio query を生成 - Text='{Text}', SpeakerId={SpeakerId}, Speed={Speed}, Pitch={Pitch}, Volume={Volume}",
                request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

            var url = BuildAudioQueryUrl(request, _settings.BaseUrl);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await SendAsync(httpRequest, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            _logger.LogDebug("Audio query を取得しました - Length: {Length} chars", content.Length);
            return ApplyVoiceParameters(content, request);
        }, cancellationToken);
    }

    /// <summary>
    /// /audio_query エンドポイント URL を組み立てる純粋関数 (テスト容易性のために分離)。
    /// </summary>
    /// <remarks>
    /// VOICEVOX の /audio_query は text と speaker しか受け付けない (speed_scale 等をクエリに付けても無視される)。
    /// 話速・音高・音量は <see cref="ApplyVoiceParameters"/> で返却 JSON 側に設定する。
    /// </remarks>
    public static string BuildAudioQueryUrl(VoiceRequest request, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseUrl);

        var encodedText = Uri.EscapeDataString(request.Text ?? String.Empty);
        return $"{baseUrl}/audio_query?text={encodedText}&speaker={request.SpeakerId}";
    }

    /// <summary>
    /// /audio_query が返した JSON の speedScale / pitchScale / volumeScale を request の値で上書きする純粋関数。
    /// </summary>
    /// <remarks>
    /// 値は <see cref="Cache.CacheKeyGenerator.NormalizeParameter"/> でキャッシュキーと同じ粒度に丸めて設定する
    /// (キーと実際に合成されるパラメータを必ず一致させる)。その他のフィールドはエンジンの返却値をそのまま保つ。
    /// </remarks>
    public static string ApplyVoiceParameters(string audioQueryJson, VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = JsonNode.Parse(audioQueryJson)?.AsObject()
            ?? throw new JsonException("audio_query の応答が JSON オブジェクトではありません");

        query["speedScale"] = Cache.CacheKeyGenerator.NormalizeParameter(request.Speed);
        query["pitchScale"] = Cache.CacheKeyGenerator.NormalizeParameter(request.Pitch);
        query["volumeScale"] = Cache.CacheKeyGenerator.NormalizeParameter(request.Volume);

        return query.ToJsonString();
    }

    public Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId, CancellationToken cancellationToken = default)
    {
        return ExecuteApiAsync("synthesize audio", async ct =>
        {
            _logger.LogDebug("音声合成リクエストを送信 - speaker={SpeakerId}, audioQueryLength={Length}",
                speakerId, audioQuery.Length);

            var url = $"{_settings.BaseUrl}/synthesis?speaker={speakerId}";
            using var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var response = await SendAsync(httpRequest, ct);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }, cancellationToken);
    }

    /// <summary>
    /// API 呼び出しの共通ラッパ: リトライ + エラーハンドリング + 例外変換。
    /// </summary>
    /// <remarks>
    /// 例外の流れ:
    ///   1. <see cref="VoicevoxRunCachedException"/> およびその派生 (<see cref="VoiceVoxApiException"/> 等) は素通し。
    ///      二重 wrap を防ぐ — 既にドメイン例外なら呼び出し元はそれを期待してハンドルする。
    ///   2. ユーザーキャンセル (<see cref="CancellationToken.IsCancellationRequested"/> = true) も素通し。
    ///      <see cref="CommandRouter"/> 側で終了コード <see cref="ErrorCodes.CancelledExitCode"/> (130) として処理する。
    ///   3. その他 (HTTP/JSON/タイムアウト等) は <see cref="MapToApiException"/> で <see cref="VoiceVoxApiException"/> に変換。
    /// </remarks>
    private async Task<T> ExecuteApiAsync<T>(string operationName, Func<CancellationToken, Task<T>> apiCall, CancellationToken cancellationToken)
    {
        try
        {
            return await _retryPolicyService.ExecuteWithRetryAsync(() => apiCall(cancellationToken), operationName, cancellationToken);
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("{OperationName} がキャンセルされました", operationName);
            throw;
        }
        catch (Exception ex)
        {
            throw MapToApiException(operationName, ex);
        }
    }

    /// <summary>
    /// 既知の例外を <see cref="VoiceVoxApiException"/> に変換する。
    /// </summary>
    /// <remarks>
    /// userMessage は元例外の Message をそのまま使う。HTTP status / 例外型から推測した
    /// 「親切メッセージ」は誤誘導になるため作らない (例: IOException = ファイル操作失敗とは限らない)。
    /// </remarks>
    private VoiceVoxApiException MapToApiException(string operationName, Exception ex)
    {
        _logger.LogError(ex, "{OperationName} に失敗しました (StatusCode: {StatusCode})",
            operationName, (ex as HttpRequestException)?.StatusCode);

        // message は内部 (Exception.Message / ログ) 用、userMessage は CLI 表示用
        return new VoiceVoxApiException(
            MapToErrorCode(ex),
            ex.Message,
            $"{operationName} に失敗しました: {ex.Message}",
            ex);
    }

    /// <summary>
    /// リトライ後に残った例外をエラーコードに対応付ける純粋関数 (テスト容易性のために分離)。
    /// </summary>
    public static string MapToErrorCode(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } statusCode } => MapHttpStatusToErrorCode(statusCode),
        // ステータスが無い = 接続できない・応答が途中で切れた (HttpIOException) など、エンジンから HTTP の応答を得られなかった
        HttpRequestException or IOException => ErrorCodes.Engine.EngineNotAvailable,
        TaskCanceledException => ErrorCodes.Api.ApiTimeout,
        JsonException => ErrorCodes.Api.ApiResponseInvalid,
        _ => ErrorCodes.General.UnknownError,
    };

    private static string MapHttpStatusToErrorCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => ErrorCodes.Api.ApiRequestFailed,
        HttpStatusCode.Unauthorized => ErrorCodes.Api.ApiAuthenticationError,
        HttpStatusCode.Forbidden => ErrorCodes.General.PermissionDenied,
        HttpStatusCode.NotFound => ErrorCodes.General.ResourceNotFound,
        HttpStatusCode.RequestTimeout => ErrorCodes.Api.ApiTimeout,
        HttpStatusCode.TooManyRequests => ErrorCodes.Api.ApiRateLimitExceeded,
        HttpStatusCode.InternalServerError => ErrorCodes.Engine.EngineProcessError,
        HttpStatusCode.ServiceUnavailable => ErrorCodes.Engine.EngineNotAvailable,
        HttpStatusCode.GatewayTimeout => ErrorCodes.Api.ApiTimeout,
        _ => ErrorCodes.Api.ApiRequestFailed,
    };

    /// <summary>
    /// エンジンそのものに届かない失敗か (接続できない・503・応答の途中切断 = EngineNotAvailable、タイムアウト = ApiTimeout)。
    /// 続けても同じ失敗を繰り返すだけなので、残りの合成 (読み上げの <see cref="SegmentProducer"/>・<c>--init</c> の
    /// <see cref="FillerManager"/>) を中止する判定に使う。どちらもプロセス間ロックを持っているので、1 件ごとに
    /// タイムアウト (ConnectionTimeout) を待つと後続の読み上げも待たせてしまう。
    /// </summary>
    /// <remarks>
    /// 500 (EngineProcessError) は特定の文だけで起きることがあるので含めない (次の文は合成できる可能性がある)。
    /// </remarks>
    public static bool IsEngineUnavailable(Exception ex)
        => ex is VoicevoxRunCachedException { ErrorCode: ErrorCodes.Engine.EngineNotAvailable or ErrorCodes.Api.ApiTimeout };

    /// <param name="request">送るリクエスト</param>
    /// <param name="cancellationToken">ユーザーのキャンセル (Ctrl+C)</param>
    /// <param name="applyTimeout">false なら ConnectionTimeout で打ち切らず、応答が来るまで待つ</param>
    /// <remarks>
    /// 応答本文は SendAsync の中で読み終わる (既定の <see cref="HttpCompletionOption.ResponseContentRead"/>) ので、
    /// タイムアウトは本文の受信まで含む (HttpClient.Timeout と同じ範囲)。
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool applyTimeout = true)
    {
        _logger.LogDebug("HTTP {Method} {RequestUri}", request.Method, request.RequestUri);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (applyTimeout)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ConnectionTimeout));
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (applyTimeout && !cancellationToken.IsCancellationRequested)
        {
            // ユーザーのキャンセルではなくタイムアウト (タイムアウトを掛けていないとき (/initialize_speaker) は、
            // HttpClient の破棄などタイムアウト以外の理由なので変換しない)。HttpClient.Timeout で打ち切られたときと同じく
            // TaskCanceledException にする (MapToErrorCode が ApiTimeout にし、RetryPolicyService はリトライしない)
            throw new TaskCanceledException($"{_settings.ConnectionTimeout} 秒以内にエンジンから応答がありませんでした", ex);
        }
        _logger.LogDebug("HTTP response: {StatusCode}", response.StatusCode);

        try
        {
            // .NET 5+ の EnsureSuccessStatusCode は HttpRequestException に StatusCode を含めて投げる
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // 失敗時は呼び出し元の using に到達しないので、ローカル所有のまま明示 Dispose する
            response.Dispose();
            throw;
        }
        return response;
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
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
            Timeout = TimeSpan.FromSeconds(_settings.ConnectionTimeout)
        };

        _logger.LogDebug("VoiceVoxApiClient を初期化しました - BaseUrl: {BaseUrl}, Timeout: {Timeout}s",
            _settings.BaseUrl, _settings.ConnectionTimeout);
    }

    public string BaseUrl => _settings.BaseUrl;
    public int ConnectionTimeout => _settings.ConnectionTimeout;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
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
            using var response = await _httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null, ct);
            response.EnsureSuccessStatusCode();
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

            var encodedText = Uri.EscapeDataString(request.Text);
            var queryString = $"text={encodedText}&speaker={request.SpeakerId}";

            // 浮動小数点の誤差を考慮し、デフォルトに近い値はクエリに含めない
            if (Math.Abs(request.Speed - 1.0) > 0.0001)
                queryString += $"&speed_scale={request.Speed}";
            if (Math.Abs(request.Pitch - 0.0) > 0.0001)
                queryString += $"&pitch_scale={request.Pitch}";
            if (Math.Abs(request.Volume - 1.0) > 0.0001)
                queryString += $"&volume_scale={request.Volume}";

            var url = $"{_settings.BaseUrl}/audio_query?{queryString}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await SendAsync(httpRequest, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            _logger.LogDebug("Audio query を取得しました - Length: {Length} chars", content.Length);
            return content;
        }, cancellationToken);
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
    /// ユーザーキャンセル (cancellationToken.IsCancellationRequested) は素のまま再 throw して
    /// CommandRouter 側で exit code 0 として処理させる。HTTP 由来のタイムアウト (TaskCanceledException
    /// で IsCancellationRequested = false) は MapToApiException で API timeout に変換する。
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
    private VoiceVoxApiException MapToApiException(string operationName, Exception ex)
    {
        switch (ex)
        {
            case HttpRequestException httpEx when httpEx.StatusCode.HasValue:
                {
                    var statusCode = httpEx.StatusCode.Value;
                    _logger.LogError(httpEx, "{OperationName} に失敗 - StatusCode: {StatusCode}", operationName, statusCode);
                    return new VoiceVoxApiException(
                        ErrorHandlingUtility.GetErrorCodeFromHttpStatus(statusCode),
                        $"{operationName} failed",
                        ErrorHandlingUtility.GetUserFriendlyMessageFromHttpStatus(statusCode),
                        httpEx,
                        statusCode,
                        null,
                        ErrorHandlingUtility.GetSuggestedSolutionFromHttpStatus(statusCode));
                }

            case TaskCanceledException tcEx:
                _logger.LogError(tcEx, "{OperationName} がタイムアウトしました", operationName);
                return new VoiceVoxApiException(
                    ErrorCodes.Api.ApiTimeout,
                    $"{operationName} timed out: {tcEx.Message}",
                    $"{operationName} がタイムアウトしました。",
                    tcEx,
                    null,
                    null,
                    "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。");

            case JsonException jsonEx:
                _logger.LogError(jsonEx, "{OperationName} レスポンスのJSON解析に失敗しました", operationName);
                return new VoiceVoxApiException(
                    ErrorCodes.Api.ApiResponseInvalid,
                    $"Failed to parse {operationName} response: {jsonEx.Message}",
                    $"{operationName} の解析に失敗しました。",
                    jsonEx,
                    null,
                    null,
                    "VOICEVOXエンジンのバージョンを確認し、必要に応じて更新してください。");

            default:
                _logger.LogError(ex, "{OperationName} 中に予期しないエラーが発生しました", operationName);
                return new VoiceVoxApiException(
                    ErrorCodes.General.UnknownError,
                    $"Unexpected error during {operationName}: {ex.Message}",
                    $"{operationName} 中に予期しないエラーが発生しました。",
                    ex,
                    null,
                    null,
                    "アプリケーションを再起動し、問題が続く場合はログを確認してください。");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("HTTP {Method} {RequestUri}", request.Method, request.RequestUri);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        _logger.LogDebug("HTTP response: {StatusCode}", response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}", null, response.StatusCode);
        }

        return response;
    }
}

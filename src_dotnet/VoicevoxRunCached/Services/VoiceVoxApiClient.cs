using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class VoiceVoxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RetryPolicyService _retryPolicyService;
    private readonly VoiceVoxSettings _settings;

    public VoiceVoxApiClient(VoiceVoxSettings settings, RetryPolicyService? retryPolicyService = null)
    {
        // C# 13 nameof expression for type-safe parameter validation
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _retryPolicyService = retryPolicyService ?? new RetryPolicyService();

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_settings.ConnectionTimeout)
        };

        Log.Information("VoiceVoxApiClient を初期化しました - BaseUrl: {BaseUrl}, Timeout: {Timeout}s", _settings.BaseUrl, _settings.ConnectionTimeout);
    }

    public string BaseUrl => _settings.BaseUrl;
    public int ConnectionTimeout => _settings.ConnectionTimeout;

    // C# 13 Enhanced auto-property with JsonSerializerOptions
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

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

    public async Task<List<Speaker>> GetSpeakersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.BaseUrl}/speakers");
            var response = await SendRequestAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var speakers = JsonSerializer.Deserialize<List<Speaker>>(content, JsonOptions);
            return speakers ?? [];
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            Log.Error(ex, "Failed to get speakers from VoiceVox API - StatusCode: {StatusCode}", ex.StatusCode);
            throw CreateVoiceVoxApiExceptionFromHttpStatus(ex.StatusCode.Value, "Failed to get speakers", ex);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("Speakers取得がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Speakers retrieval was cancelled",
                "スピーカー情報の取得がキャンセルされました。",
                ex,
                "操作を再実行してください。"
            );
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "Speakers取得がタイムアウトしました");
            throw new VoiceVoxApiException(
                ErrorCodes.Api.ApiTimeout,
                $"Speakers retrieval timed out: {ex.Message}",
                "スピーカー情報の取得がタイムアウトしました。",
                null,
                null,
                "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"
            );
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "SpeakersレスポンスのJSON解析に失敗しました");
            throw new VoiceVoxApiException(
                ErrorCodes.Api.ApiResponseInvalid,
                $"Failed to parse speakers response: {ex.Message}",
                "スピーカー情報の解析に失敗しました。",
                null,
                null,
                "VOICEVOXエンジンのバージョンを確認し、必要に応じて更新してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error while getting speakers");
            throw new VoiceVoxApiException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error while getting speakers: {ex.Message}",
                "スピーカー情報の取得中に予期しないエラーが発生しました。",
                ex,
                null,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    public async Task InitializeSpeakerAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        Log.Debug("スピーカー {SpeakerId} を初期化中...", speakerId);
        await _retryPolicyService.ExecuteWithRetryAsync(async () =>
        {
            return await ExecuteApiCallAsync(async () =>
            {
                var response = await _httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null, cancellationToken);
                response.EnsureSuccessStatusCode();
                Log.Information("スピーカー {SpeakerId} の初期化が完了しました", speakerId);
                return true; // Return value for generic method
            }, $"Failed to initialize speaker {speakerId}");
        }, $"スピーカー {speakerId} 初期化");
    }

    public async Task<string> GenerateAudioQueryAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // デバッグ用のログ出力
            Log.Information("Generating audio query for request: Text='{Text}', SpeakerId={SpeakerId}, Speed={Speed}, Pitch={Pitch}, Volume={Volume}",
                request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

            // VoiceVox APIは/audio_queryエンドポイントでクエリパラメータとしてtextとspeakerを受け取る
            var encodedText = Uri.EscapeDataString(request.Text);
            var queryString = $"text={encodedText}&speaker={request.SpeakerId}";

            // 追加パラメータがある場合は含める（浮動小数点の誤差を考慮）
            if (Math.Abs(request.Speed - 1.0) > 0.0001)
                queryString += $"&speed_scale={request.Speed}";
            if (Math.Abs(request.Pitch - 0.0) > 0.0001)
                queryString += $"&pitch_scale={request.Pitch}";
            if (Math.Abs(request.Volume - 1.0) > 0.0001)
                queryString += $"&volume_scale={request.Volume}";

            var url = $"{_settings.BaseUrl}/audio_query?{queryString}";
            Log.Information("Request URL: {Url}", url);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await SendRequestAsync(httpRequest, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            Log.Debug("Audio query generated successfully - Length: {Length} characters", content.Length);
            return content;
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            Log.Error(ex, "Failed to generate audio query - StatusCode: {StatusCode}", ex.StatusCode);
            throw CreateVoiceVoxApiExceptionFromHttpStatus(ex.StatusCode.Value, "Failed to generate audio query", ex);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("Audio query生成がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Audio query generation was cancelled",
                "音声クエリの生成がキャンセルされました。",
                ex,
                "操作を再実行してください。"
            );
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "Audio query生成がタイムアウトしました");
            throw new VoiceVoxApiException(
                ErrorCodes.Api.ApiTimeout,
                $"Audio query generation timed out: {ex.Message}",
                "音声クエリの生成がタイムアウトしました。",
                null,
                null,
                "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"
            );
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Audio queryレスポンスのJSON解析に失敗しました");
            throw new VoiceVoxApiException(
                ErrorCodes.Api.ApiResponseInvalid,
                $"Failed to parse audio query response: {ex.Message}",
                "音声クエリの解析に失敗しました。",
                null,
                null,
                "VOICEVOXエンジンのバージョンを確認し、必要に応じて更新してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Audio query生成中に予期しないエラーが発生しました");
            throw new VoiceVoxApiException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error during audio query generation: {ex.Message}",
                "音声クエリの生成中に予期しないエラーが発生しました。",
                ex,
                null,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId, CancellationToken cancellationToken = default)
    {
        try
        {
            // デバッグ用のログ出力
            Log.Information("Synthesizing audio for speaker {SpeakerId} with audioQuery length: {Length}", speakerId, audioQuery.Length);

            // VoiceVox APIの/synthesisエンドポイントはクエリパラメータでspeakerを受け取り、ボディでaudio_queryを受け取る
            var url = $"{_settings.BaseUrl}/synthesis?speaker={speakerId}";
            Log.Information("Synthesis URL: {Url}", url);

            var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            Log.Information("Sending synthesis request to VoiceVox API...");
            var response = await SendRequestAsync(httpRequest, cancellationToken);
            Log.Information("Synthesis response received, status: {StatusCode}", response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            Log.Error(ex, "Failed to synthesize audio - SpeakerId: {SpeakerId}, StatusCode: {StatusCode}", speakerId, ex.StatusCode);
            throw CreateVoiceVoxApiExceptionFromHttpStatus(ex.StatusCode.Value, "Failed to synthesize audio", ex);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("音声合成がキャンセルされました - SpeakerId: {SpeakerId}", speakerId);
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Audio synthesis was cancelled",
                "音声合成がキャンセルされました。",
                ex,
                "操作を再実行してください。"
            );
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "音声合成がタイムアウトしました - SpeakerId: {SpeakerId}", speakerId);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.ApiTimeout,
                $"Audio synthesis timed out: {ex.Message}",
                "音声合成がタイムアウトしました。",
                null,
                null,
                "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音声合成中に予期しないエラーが発生しました - SpeakerId: {SpeakerId}", speakerId);
            throw new VoiceVoxApiException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error during audio synthesis: {ex.Message}",
                "音声合成中に予期しないエラーが発生しました。",
                ex,
                null,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    // C# 13 Enhanced helper method with generic return type
    private async Task<T> ExecuteApiCallAsync<T>(Func<Task<T>> apiCall, string errorMessage)
    {
        try
        {
            return await apiCall();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{errorMessage}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"{errorMessage}: Request timed out", ex);
        }
    }

    /// <summary>
    /// HTTPステータスコードからVoiceVoxApiExceptionを生成します
    /// </summary>
    private static VoiceVoxApiException CreateVoiceVoxApiExceptionFromHttpStatus(
        HttpStatusCode statusCode, string operation, Exception? innerException = null)
    {
        var errorCode = ErrorHandlingUtility.GetErrorCodeFromHttpStatus(statusCode);
        var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromHttpStatus(statusCode);
        var solution = ErrorHandlingUtility.GetSuggestedSolutionFromHttpStatus(statusCode);

        if (innerException != null)
        {
            return new VoiceVoxApiException(errorCode, $"{operation} failed", userMessage,
                innerException, statusCode, null, solution);
        }
        else
        {
            return new VoiceVoxApiException(errorCode, $"{operation} failed", userMessage,
                statusCode, null, solution);
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Information("Sending HTTP request to: {Method} {RequestUri}", request.Method, request.RequestUri);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            Log.Information("HTTP response received: {StatusCode}", response.StatusCode);

            // 詳細なエラーハンドリング
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}", null, response.StatusCode);
            }

            return response;
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            // HTTPエラーは再スロー
            throw;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("VoiceVox API request was cancelled", null, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            throw new HttpRequestException("VoiceVox API request timed out", null, HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            // 既に詳細化されているHTTPエラーは再スロー
            throw;
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Unexpected error during VoiceVox API request: {ex.Message}", ex);
        }
    }

}

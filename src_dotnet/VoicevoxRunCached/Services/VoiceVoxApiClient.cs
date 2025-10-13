using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        _httpClient?.Dispose();
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
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Log.Error(ex, "VOICEVOXエンジンが利用できません - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Engine.ENGINE_NOT_AVAILABLE,
                $"Failed to get speakers: {ex.Message}",
                "VOICEVOXエンジンが利用できません。エンジンが起動しているか確認してください。",
                ex.StatusCode,
                null,
                "VOICEVOXエンジンを起動するか、設定のBaseUrlを確認してください。"
            );
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Error(ex, "Speakersエンドポイントが見つかりません - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Speakers endpoint not found: {ex.Message}",
                "スピーカー情報の取得に失敗しました。VOICEVOXエンジンのバージョンを確認してください。",
                ex.StatusCode,
                null,
                "VOICEVOXエンジンを最新版に更新してください。"
            );
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Failed to get speakers from VoiceVox API - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Failed to get speakers: {ex.Message}",
                "スピーカー情報の取得に失敗しました。",
                ex.StatusCode,
                null,
                "ネットワーク接続とVOICEVOXエンジンの状態を確認してください。"
            );
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("Speakers取得がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OPERATION_CANCELLED,
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
                ErrorCodes.Api.API_TIMEOUT,
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
                ErrorCodes.Api.API_RESPONSE_INVALID,
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
                ErrorCodes.General.UNKNOWN_ERROR,
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

            // 追加パラメータがある場合は含める
            if (request.Speed != 1.0)
                queryString += $"&speed_scale={request.Speed}";
            if (request.Pitch != 0.0)
                queryString += $"&pitch_scale={request.Pitch}";
            if (request.Volume != 1.0)
                queryString += $"&volume_scale={request.Volume}";

            var url = $"{_settings.BaseUrl}/audio_query?{queryString}";
            Log.Information("Request URL: {Url}", url);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            var response = await SendRequestAsync(httpRequest, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            Log.Debug("Audio query generated successfully - Length: {Length} characters", content.Length);
            return content;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            Log.Error(ex, "Audio query生成リクエストが無効です - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Invalid audio query request: {ex.Message}",
                "音声クエリの生成リクエストが無効です。テキスト内容とパラメータを確認してください。",
                ex.StatusCode,
                null,
                "テキストが空でないこと、スピーカーIDが有効であることを確認してください。"
            );
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Log.Error(ex, "VOICEVOXエンジンが利用できません - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Engine.ENGINE_NOT_AVAILABLE,
                $"VOICEVOX engine unavailable: {ex.Message}",
                "VOICEVOXエンジンが利用できません。エンジンが起動しているか確認してください。",
                ex.StatusCode,
                null,
                "VOICEVOXエンジンを起動するか、設定のBaseUrlを確認してください。"
            );
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Audio query生成に失敗しました - StatusCode: {StatusCode}", ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Failed to generate audio query: {ex.Message}",
                "音声クエリの生成に失敗しました。",
                ex.StatusCode,
                null,
                "ネットワーク接続とVOICEVOXエンジンの状態を確認してください。"
            );
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("Audio query生成がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OPERATION_CANCELLED,
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
                ErrorCodes.Api.API_TIMEOUT,
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
                ErrorCodes.Api.API_RESPONSE_INVALID,
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
                ErrorCodes.General.UNKNOWN_ERROR,
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
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            Log.Error(ex, "音声合成リクエストが無効です - SpeakerId: {SpeakerId}, StatusCode: {StatusCode}", speakerId, ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Invalid synthesis request: {ex.Message}",
                "音声合成リクエストが無効です。スピーカーIDと音声クエリを確認してください。",
                ex.StatusCode,
                null,
                "スピーカーIDが有効であること、音声クエリが正しい形式であることを確認してください。"
            );
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Log.Error(ex, "VOICEVOXエンジンが利用できません - SpeakerId: {SpeakerId}, StatusCode: {StatusCode}", speakerId, ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Engine.ENGINE_NOT_AVAILABLE,
                $"VOICEVOX engine unavailable: {ex.Message}",
                "VOICEVOXエンジンが利用できません。エンジンが起動しているか確認してください。",
                ex.StatusCode,
                null,
                "VOICEVOXエンジンを起動するか、設定のBaseUrlを確認してください。"
            );
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.InternalServerError)
        {
            Log.Error(ex, "VOICEVOXエンジンで内部エラーが発生しました - SpeakerId: {SpeakerId}, StatusCode: {StatusCode}", speakerId, ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Engine.ENGINE_PROCESS_ERROR,
                $"VOICEVOX engine internal error: {ex.Message}",
                "VOICEVOXエンジンで内部エラーが発生しました。",
                ex.StatusCode,
                null,
                "VOICEVOXエンジンを再起動してください。問題が続く場合は、エンジンのログを確認してください。"
            );
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "音声合成に失敗しました - SpeakerId: {SpeakerId}, StatusCode: {StatusCode}", speakerId, ex.StatusCode);
            throw new VoiceVoxApiException(
                ErrorCodes.Api.API_REQUEST_FAILED,
                $"Failed to synthesize audio: {ex.Message}",
                "音声合成に失敗しました。",
                ex.StatusCode,
                null,
                "ネットワーク接続とVOICEVOXエンジンの状態を確認してください。"
            );
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("音声合成がキャンセルされました - SpeakerId: {SpeakerId}", speakerId);
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OPERATION_CANCELLED,
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
                ErrorCodes.Api.API_TIMEOUT,
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
                ErrorCodes.General.UNKNOWN_ERROR,
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
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorMessage = GetUserFriendlyErrorMessage(response.StatusCode, errorContent);
                throw new HttpRequestException(errorMessage, null, response.StatusCode);
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

    private string GetUserFriendlyErrorMessage(HttpStatusCode statusCode, string? errorContent)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => $"VoiceVox API request was invalid. Please check your parameters. Details: {errorContent}",
            HttpStatusCode.Unauthorized => "VoiceVox API authentication failed. Please check your API key or permissions.",
            HttpStatusCode.Forbidden => "VoiceVox API access denied. Please check your permissions.",
            HttpStatusCode.NotFound => "VoiceVox API endpoint not found. Please check the API version and endpoint.",
            HttpStatusCode.TooManyRequests => "VoiceVox API rate limit exceeded. Please wait before making another request.",
            HttpStatusCode.InternalServerError => $"VoiceVox API server error. Please try again later. Details: {errorContent}",
            HttpStatusCode.ServiceUnavailable => "VoiceVox API service is temporarily unavailable. Please try again later.",
            HttpStatusCode.GatewayTimeout => "VoiceVox API request timed out. Please try again later.",
            _ => $"VoiceVox API error (HTTP {statusCode}). Details: {errorContent}"
        };
    }

    private static string ApplyVoiceParametersToAudioQueryJson(string audioQueryJson, VoiceRequest request)
    {
        try
        {
            var node = JsonNode.Parse(audioQueryJson) as JsonObject;
            if (node == null)
                return audioQueryJson;

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            var speed = Clamp(request.Speed, 0.5, 2.0);
            var pitch = Clamp(request.Pitch, -0.15, 0.15);
            var volume = Clamp(request.Volume, 0.0, 2.0);

            node["speedScale"] = speed;
            node["pitchScale"] = pitch;
            node["volumeScale"] = volume;

            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return audioQueryJson;
        }
    }
}

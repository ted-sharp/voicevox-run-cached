using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using Serilog;

namespace VoicevoxRunCached.Services;

public class VoiceVoxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VoiceVoxSettings _settings;
    private readonly RetryPolicyService _retryPolicyService;

    public string BaseUrl => this._settings.BaseUrl;
    public int ConnectionTimeout => this._settings.ConnectionTimeout;

    public VoiceVoxApiClient(VoiceVoxSettings settings, RetryPolicyService? retryPolicyService = null)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._retryPolicyService = retryPolicyService ?? new RetryPolicyService();

        this._httpClient = new HttpClient
        {
            BaseAddress = new Uri(this._settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(this._settings.ConnectionTimeout)
        };

        Log.Information("VoiceVoxApiClient を初期化しました - BaseUrl: {BaseUrl}, Timeout: {Timeout}s", this._settings.BaseUrl, this._settings.ConnectionTimeout);
    }



    public async Task<List<Speaker>> GetSpeakersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{this._settings.BaseUrl}/speakers");
            var response = await this.SendRequestAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var speakers = JsonSerializer.Deserialize<List<Speaker>>(content, JsonOptions);
            return speakers ?? [];
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Failed to get speakers from VoiceVox API");
            throw;
        }
    }

    // C# 13 Enhanced auto-property with JsonSerializerOptions
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task InitializeSpeakerAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        Log.Debug("スピーカー {SpeakerId} を初期化中...", speakerId);
        await this._retryPolicyService.ExecuteWithRetryAsync(async () =>
        {
            return await this.ExecuteApiCallAsync(async () =>
            {
                var response = await this._httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null, cancellationToken);
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
            var queryRequest = new
            {
                text = request.Text,
                speaker = request.SpeakerId,
                speedScale = request.Speed,
                pitchScale = request.Pitch,
                volumeScale = request.Volume
            };

            var json = JsonSerializer.Serialize(queryRequest, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this._settings.BaseUrl}/audio_query")
            {
                Content = content
            };

            var response = await this.SendRequestAsync(httpRequest, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Failed to generate audio query from VoiceVox API for text: {Text}", request.Text);
            throw;
        }
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var synthesisRequest = new
            {
                audio_query = JsonSerializer.Deserialize<object>(audioQuery),
                speaker = speakerId
            };

            var json = JsonSerializer.Serialize(synthesisRequest, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this._settings.BaseUrl}/synthesis")
            {
                Content = content
            };

            var response = await this.SendRequestAsync(httpRequest, cancellationToken);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Failed to synthesize audio from VoiceVox API for speaker: {SpeakerId}", speakerId);
            throw;
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
            var response = await this._httpClient.SendAsync(request, cancellationToken);

            // 詳細なエラーハンドリング
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorMessage = this.GetUserFriendlyErrorMessage(response.StatusCode, errorContent);
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

    public void Dispose()
    {
        this._httpClient?.Dispose();
    }

    private static string ApplyVoiceParametersToAudioQueryJson(string audioQueryJson, VoiceRequest request)
    {
        try
        {
            var node = JsonNode.Parse(audioQueryJson) as JsonObject;
            if (node == null) return audioQueryJson;

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

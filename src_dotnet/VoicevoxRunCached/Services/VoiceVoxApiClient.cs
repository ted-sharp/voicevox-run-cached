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
        Log.Debug("スピーカー一覧を取得中...");
        return await _retryPolicyService.ExecuteWithRetryAsync(async () =>
        {
            return await this.ExecuteApiCallAsync(async () =>
            {
                var response = await this._httpClient.GetAsync("/speakers", cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var speakers = JsonSerializer.Deserialize<List<Speaker>>(json, JsonOptions);

                Log.Information("スピーカー一覧を取得しました - 数: {Count}", speakers?.Count ?? 0);
                return speakers ?? new List<Speaker>();
            }, "Failed to get speakers from VOICEVOX API");
        }, "スピーカー一覧取得");
    }

    // C# 13 Enhanced auto-property with JsonSerializerOptions
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task InitializeSpeakerAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        Log.Debug("スピーカー {SpeakerId} を初期化中...", speakerId);
        await _retryPolicyService.ExecuteWithRetryAsync(async () =>
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
        Log.Debug("音声クエリを生成中... - テキスト: {Text}, スピーカー: {SpeakerId}", request.Text, request.SpeakerId);
        return await _retryPolicyService.ExecuteWithRetryAsync(async () =>
        {
            var json = await this.ExecuteApiCallAsync(async () =>
            {
                var encodedText = Uri.EscapeDataString(request.Text);
                var url = $"/audio_query?text={encodedText}&speaker={request.SpeakerId}";

                var response = await this._httpClient.PostAsync(url, null, cancellationToken);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync();
            }, "Failed to generate audio query");

            var modifiedJson = ApplyVoiceParametersToAudioQueryJson(json, request);
            Log.Debug("音声クエリの生成が完了しました - テキスト長: {TextLength}", request.Text.Length);
            return modifiedJson;
        }, "音声クエリ生成");
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId, CancellationToken cancellationToken = default)
    {
        Log.Debug("音声合成中... - スピーカー: {SpeakerId}", speakerId);
        return await _retryPolicyService.ExecuteWithRetryAsync(async () =>
        {
            return await this.ExecuteApiCallAsync(async () =>
            {
                var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
                var url = $"/synthesis?speaker={speakerId}";

                var response = await this._httpClient.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var audioData = await response.Content.ReadAsByteArrayAsync();
                Log.Information("音声合成が完了しました - スピーカー: {SpeakerId}, サイズ: {Size} bytes", speakerId, audioData.Length);
                return audioData;
            }, "Failed to synthesize audio");
        }, "音声合成");
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

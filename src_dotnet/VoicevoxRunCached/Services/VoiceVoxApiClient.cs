using System.Text;
using System.Text.Json;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class VoiceVoxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VoiceVoxSettings _settings;

    public string BaseUrl => this._settings.BaseUrl;
    public int ConnectionTimeout => this._settings.ConnectionTimeout;

    public VoiceVoxApiClient(VoiceVoxSettings settings)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._httpClient = new HttpClient
        {
            BaseAddress = new Uri(this._settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(this._settings.ConnectionTimeout)
        };
    }

    private static async Task<T> WithSimpleRetryAsync<T>(Func<Task<T>> action, int maxAttempts = 2, int delayMs = 250)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try { return await action(); }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == maxAttempts) break;
                await Task.Delay(delayMs);
            }
        }
        throw last ?? new InvalidOperationException("Unknown error in retry block");
    }

    public async Task<List<Speaker>> GetSpeakersAsync()
    {
        return await this.ExecuteApiCallAsync(async () =>
        {
            var response = await WithSimpleRetryAsync(async () => await this._httpClient.GetAsync("/speakers"));
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var speakers = JsonSerializer.Deserialize<List<Speaker>>(json, JsonOptions);

            return speakers ?? new List<Speaker>();
        }, "Failed to get speakers from VOICEVOX API");
    }

    // C# 13 Enhanced auto-property with JsonSerializerOptions
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task InitializeSpeakerAsync(int speakerId)
    {
        await this.ExecuteApiCallAsync(async () =>
        {
            var response = await WithSimpleRetryAsync(async () => await this._httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null));
            response.EnsureSuccessStatusCode();
            return true; // Return value for generic method
        }, $"Failed to initialize speaker {speakerId}");
    }

    public async Task<string> GenerateAudioQueryAsync(VoiceRequest request)
    {
        return await this.ExecuteApiCallAsync(async () =>
        {
            var encodedText = Uri.EscapeDataString(request.Text);
            var url = $"/audio_query?text={encodedText}&speaker={request.SpeakerId}";

            var response = await WithSimpleRetryAsync(async () => await this._httpClient.PostAsync(url, null));
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }, "Failed to generate audio query");
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId)
    {
        return await this.ExecuteApiCallAsync(async () =>
        {
            var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
            var url = $"/synthesis?speaker={speakerId}";

            var response = await WithSimpleRetryAsync(async () => await this._httpClient.PostAsync(url, content));
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync();
        }, "Failed to synthesize audio");
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
}

using System.Text;
using System.Text.Json;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class VoiceVoxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VoiceVoxSettings _settings;
    
    // C# 13 Enhanced auto-properties with connection checking
    private bool _isConnected;
    public bool IsConnected 
    { 
        get => _isConnected;
        private set => _isConnected = CheckConnection();
    }
    public string BaseUrl => _settings.BaseUrl;
    public int ConnectionTimeout => _settings.ConnectionTimeout;

    public VoiceVoxApiClient(VoiceVoxSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_settings.ConnectionTimeout)
        };
    }
    
    private bool CheckConnection()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = _httpClient.GetAsync("/speakers", cts.Token).Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<Speaker>> GetSpeakersAsync()
    {
        return await ExecuteApiCallAsync(async () =>
        {
            var response = await _httpClient.GetAsync("/speakers");
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
        await ExecuteApiCallAsync(async () =>
        {
            var response = await _httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null);
            response.EnsureSuccessStatusCode();
            return true; // Return value for generic method
        }, $"Failed to initialize speaker {speakerId}");
    }

    public async Task<string> GenerateAudioQueryAsync(VoiceRequest request)
    {
        return await ExecuteApiCallAsync(async () =>
        {
            var encodedText = Uri.EscapeDataString(request.Text);
            var url = $"/audio_query?text={encodedText}&speaker={request.SpeakerId}";
            
            var response = await _httpClient.PostAsync(url, null);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsStringAsync();
        }, "Failed to generate audio query");
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId)
    {
        return await ExecuteApiCallAsync(async () =>
        {
            var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
            var url = $"/synthesis?speaker={speakerId}";
            
            var response = await _httpClient.PostAsync(url, content);
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
        _httpClient?.Dispose();
    }
}
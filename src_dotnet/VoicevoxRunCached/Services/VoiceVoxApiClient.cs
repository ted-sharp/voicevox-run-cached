using System.Text;
using System.Text.Json;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class VoiceVoxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VoiceVoxSettings _settings;

    public VoiceVoxApiClient(VoiceVoxSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_settings.ConnectionTimeout)
        };
    }

    public async Task<List<Speaker>> GetSpeakersAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/speakers");
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var speakers = JsonSerializer.Deserialize<List<Speaker>>(json, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower 
            });
            
            return speakers ?? new List<Speaker>();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to get speakers from VOICEVOX API: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException("Request to VOICEVOX API timed out", ex);
        }
    }

    public async Task InitializeSpeakerAsync(int speakerId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"/initialize_speaker?speaker={speakerId}", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to initialize speaker {speakerId}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"Initialize speaker {speakerId} request timed out", ex);
        }
    }

    public async Task<string> GenerateAudioQueryAsync(VoiceRequest request)
    {
        try
        {
            var encodedText = Uri.EscapeDataString(request.Text);
            var url = $"/audio_query?text={encodedText}&speaker={request.SpeakerId}";
            
            var response = await _httpClient.PostAsync(url, null);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to generate audio query: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException("Generate audio query request timed out", ex);
        }
    }

    public async Task<byte[]> SynthesizeAudioAsync(string audioQuery, int speakerId)
    {
        try
        {
            var content = new StringContent(audioQuery, Encoding.UTF8, "application/json");
            var url = $"/synthesis?speaker={speakerId}";
            
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to synthesize audio: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException("Synthesize audio request timed out", ex);
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
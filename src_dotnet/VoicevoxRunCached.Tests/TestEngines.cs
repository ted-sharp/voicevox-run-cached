using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// テスト用の <see cref="VoicevoxEngine"/> / <see cref="SpeechEngineProvider"/>。
/// </summary>
internal static class TestEngines
{
    /// <summary>
    /// <paramref name="apiClient"/> で合成し、エンジンの起動・停止を <paramref name="engineManager"/> で行う VOICEVOX。
    /// </summary>
    public static SpeechEngineProvider Create(VoiceVoxEngineManager engineManager, VoiceVoxApiClient apiClient)
        => new(
            new VoicevoxEngine(new VoiceVoxSettings(), engineManager, apiClient),
            new VoicepeakEngine(new VoicepeakSettings(), NullLogger.Instance),
            Cevio(new FakeCevioRemoteService()));

    /// <summary>
    /// エンジンの停止だけを見るとき (<see cref="SpeechLock"/>)。合成には使わない。
    /// </summary>
    public static SpeechEngineProvider Create(VoiceVoxEngineManager engineManager)
        => Create(engineManager, new VoiceVoxApiClient(new VoiceVoxSettings(), new RetryPolicyService(NullLogger.Instance), NullLogger.Instance));

    /// <summary>
    /// <paramref name="apiClient"/> で合成するだけの VOICEVOX (エンジンを起動しない設定)。
    /// </summary>
    public static VoicevoxEngine Voicevox(VoiceVoxApiClient apiClient)
        => new(new VoiceVoxSettings(), new VoiceVoxEngineManager(new VoiceVoxSettings(), NullLogger.Instance, SystemClock.Instance), apiClient);

    /// <summary>
    /// <paramref name="remote"/> (COM の代わり) で合成する CeVIO。ホストは既定の設定 (自動起動・常駐) で管理する。
    /// </summary>
    public static CevioEngine Cevio(FakeCevioRemoteService remote, CevioSettings? settings = null)
    {
        settings ??= new CevioSettings();
        return new(new CevioHostManager(settings, remote, NullLogger.Instance, SystemClock.Instance), remote, settings);
    }
}

/// <summary>
/// 合成すると正弦波の WAV を返すだけのエンジン。合成した文を記録する (フィラーの文の選び方を見るため)。
/// </summary>
internal sealed class StubSpeechEngine(SpeechEngineKind kind, string[]? fillerTexts = null) : ISpeechEngine
{
    public List<string> SynthesizedTexts { get; } = [];

    public SpeechEngineKind Kind => kind;

    public IEngineFillerSettings FillerSettings { get; } = new StubFillerSettings(fillerTexts ?? [], null);

    public Task PrepareAsync(string speaker, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        SynthesizedTexts.Add(request.Text);
        return Task.FromResult(TestAudio.SineWav(24000, 200));
    }

    public Task<IReadOnlyList<SpeakerInfo>> GetSpeakersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SpeakerInfo>>([]);

    public bool IsEngineUnavailable(Exception ex) => false;

    public void StopIfNotKept()
    {
    }

    public bool HoldsConnectionUntilExit => false;

    private sealed record StubFillerSettings(string[] FillerTexts, int? MaxFillersPerWait) : IEngineFillerSettings;
}

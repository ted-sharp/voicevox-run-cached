using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
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
        => new(new VoicevoxEngine(engineManager, apiClient), Cevio(new FakeCevioRemoteService()));

    /// <summary>
    /// エンジンの停止だけを見るとき (<see cref="SpeechLock"/>)。合成には使わない。
    /// </summary>
    public static SpeechEngineProvider Create(VoiceVoxEngineManager engineManager)
        => Create(engineManager, new VoiceVoxApiClient(new VoiceVoxSettings(), new RetryPolicyService(NullLogger.Instance), NullLogger.Instance));

    /// <summary>
    /// <paramref name="apiClient"/> で合成するだけの VOICEVOX (エンジンを起動しない設定)。
    /// </summary>
    public static VoicevoxEngine Voicevox(VoiceVoxApiClient apiClient)
        => new(new VoiceVoxEngineManager(new VoiceVoxSettings(), NullLogger.Instance, SystemClock.Instance), apiClient);

    /// <summary>
    /// <paramref name="remote"/> (COM の代わり) で合成する CeVIO。ホストは既定の設定 (自動起動・常駐) で管理する。
    /// </summary>
    public static CevioEngine Cevio(FakeCevioRemoteService remote, CevioSettings? settings = null)
        => new(new CevioHostManager(settings ?? new CevioSettings(), remote, NullLogger.Instance, SystemClock.Instance), remote);
}

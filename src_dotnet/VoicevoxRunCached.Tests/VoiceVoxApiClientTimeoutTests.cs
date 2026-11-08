using System.Diagnostics;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VoiceVox.ConnectionTimeout の掛かり方。/initialize_speaker だけはモデルの読み込みが終わるまで待つ。
/// </summary>
public class VoiceVoxApiClientTimeoutTests
{
    [Test]
    public async Task InitializeSpeaker_SlowerThanConnectionTimeout_WaitsForCompletion()
    {
        // 回帰: エンジン起動直後はモデルの読み込みで /initialize_speaker が遅く、ConnectionTimeout で打ち切ると
        // 最初の読み上げが失敗していた。応答 = 読み込み完了なので、応答まで待つ
        using var engine = new FakeEngineServer(initializeSpeakerDelay: TimeSpan.FromMilliseconds(1500));
        using var apiClient = engine.CreateApiClient(connectionTimeoutSeconds: 1);

        await apiClient.InitializeSpeakerAsync(FakeEngineServer.SpeakerId);
    }

    [Test]
    public async Task InitializeSpeaker_UserCancel_ThrowsOperationCanceled()
    {
        // タイムアウトで打ち切らない代わりに、Ctrl+C では止まる (タイムアウト扱いにもしない)
        using var engine = new FakeEngineServer(initializeSpeakerDelay: TimeSpan.FromSeconds(3));
        using var apiClient = engine.CreateApiClient(connectionTimeoutSeconds: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            apiClient.InitializeSpeakerAsync(FakeEngineServer.SpeakerId, cts.Token));
    }

    [Test]
    public async Task InitializeSpeaker_CanceledOtherThanByUser_IsNotReportedAsConnectionTimeout()
    {
        // タイムアウトを掛けていないので、HttpClient の破棄などで止まっても「N 秒以内に応答がありませんでした」と言わない
        using var engine = new FakeEngineServer(initializeSpeakerDelay: TimeSpan.FromSeconds(3));
        var apiClient = engine.CreateApiClient(connectionTimeoutSeconds: 1);
        var initialize = apiClient.InitializeSpeakerAsync(FakeEngineServer.SpeakerId);
        await Task.Delay(200);

        apiClient.Dispose();
        var ex = await Assert.ThrowsAsync<Exception>(() => initialize);

        await Assert.That(ex!.ToString()).DoesNotContain("秒以内");
    }

    [Test]
    public async Task AudioQuery_NoResponseWithinConnectionTimeout_FailsAsApiTimeoutWithoutRetry()
    {
        // 文ごとの合成は ConnectionTimeout で打ち切り、リトライしない (リトライすると 1 文で約 93 秒ロックを握る)
        using var engine = new FakeEngineServer(audioQueryDelay: TimeSpan.FromSeconds(3));
        using var apiClient = engine.CreateApiClient(connectionTimeoutSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            apiClient.GenerateAudioQueryAsync(new VoiceRequest("テスト", FakeEngineServer.SpeakerId)));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Api.ApiTimeout);
        // リトライしていれば 3 回分のタイムアウト (約 3 秒) を待つ (偽エンジンは要求を 1 件ずつ処理するので、2 回目以降も
        // 1 回目の応答の遅延 (3 秒) が終わるまで処理されず、それぞれ 1 秒でタイムアウトする)。負荷で遅れても落ちないよう余裕を取る
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(2500));
    }
}

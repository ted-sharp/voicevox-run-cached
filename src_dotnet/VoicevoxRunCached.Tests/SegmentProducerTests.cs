using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// エンジンに届かない失敗が起きたら残りの合成を中止する (キャッシュ済みのセグメントは流し続ける)。
/// </summary>
public class SegmentProducerTests
{
    [Test]
    public async Task EngineUnavailable_StopsSynthesizingRemainingSegments()
    {
        // 回帰: エンジンが落ちても残りの全セグメントでリトライ込みの失敗を待ち、その間プロセス間ロックを持ち続けていた
        using var engine = new FakeEngineServer(audioQueryFails: _ => true, audioQueryFailureStatus: HttpStatusCode.ServiceUnavailable);

        var (result, written) = await ProduceAsync(engine);

        // 最初の未キャッシュ (A) のリトライ分だけで、C には問い合わせない
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(AudioConstants.RetryMaxAttempts);
        // キャッシュ済みの B は再生に回る。中止した C は流さない
        await Assert.That(String.Join("|", written)).IsEqualTo("A|B");
        // 中止した分も失敗に数える (全部失敗なら終了コード 5、一部なら 8 のまま)
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 2, SuccessCount: 0, FailureCount: 2));
    }

    [Test]
    public async Task RequestError_ContinuesWithRemainingSegments()
    {
        // 400 などエンジンが応答している失敗は文ごとの問題なので、残りの合成は続ける
        using var engine = new FakeEngineServer(audioQueryFails: _ => true, audioQueryFailureStatus: HttpStatusCode.BadRequest);

        var (result, written) = await ProduceAsync(engine);

        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(2);
        await Assert.That(String.Join("|", written)).IsEqualTo("A|B|C");
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 2, SuccessCount: 0, FailureCount: 2));
    }

    /// <summary>
    /// 未キャッシュ A → キャッシュ済み B → 未キャッシュ C を流し、結果と channel に書かれたテキストを返す。
    /// </summary>
    private static async Task<(SynthesisResult Result, List<string> Written)> ProduceAsync(FakeEngineServer engine)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_producer_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var apiClient = engine.CreateApiClient();
            var cacheManager = new AudioCacheManager(
                new CacheSettings { Directory = dir, UseExecutableBaseDirectory = false }, NullLogger.Instance, SystemClock.Instance);
            var producer = new SegmentProducer(apiClient, cacheManager, NullLogger.Instance);

            List<TextSegment> segments =
            [
                new TextSegment("A"),
                new TextSegment("B") { IsCached = true, AudioData = TestAudio.SineWav(24000, 100) },
                new TextSegment("C"),
            ];
            var channel = Channel.CreateUnbounded<TextSegment>();

            var result = await producer.ProduceAsync(
                segments, channel.Writer, new VoiceRequest(speakerId: FakeEngineServer.SpeakerId), saveToCache: true, CancellationToken.None);

            var written = new List<string>();
            await foreach (var segment in channel.Reader.ReadAllAsync())
            {
                written.Add(segment.Text);
            }
            return (result, written);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}

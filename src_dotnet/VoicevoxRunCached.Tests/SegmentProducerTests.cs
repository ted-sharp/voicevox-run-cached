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

    [Test]
    public async Task DuplicateText_SynthesizesOnceAndReusesAudio()
    {
        // 回帰: 同じ文を 2 回合成し、同じキーの保存が並行して同じファイルに書き込んで共有違反になりえた
        using var engine = new FakeEngineServer();
        string[] cachedAudioFiles = [];

        var (result, written) = await ProduceAsync(
            engine,
            [new TextSegment("A"), new TextSegment("A")],
            dir => cachedAudioFiles = Directory.GetFiles(dir, "*.opus"));

        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(String.Join("|", written)).IsEqualTo("A:True|A:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 2, SuccessCount: 2, FailureCount: 0));
        await Assert.That(cachedAudioFiles.Length).IsEqualTo(1);
    }

    [Test]
    public async Task EngineUnavailable_StillPlaysTextAlreadySynthesized()
    {
        // エンジンが止まった後でも、この実行で合成済みの文は使い回して再生する
        using var engine = new FakeEngineServer(audioQueryFails: text => text == "B", audioQueryFailureStatus: HttpStatusCode.ServiceUnavailable);

        var (result, written) = await ProduceAsync(
            engine,
            [new TextSegment("A"), new TextSegment("B"), new TextSegment("A"), new TextSegment("C")]);

        await Assert.That(String.Join("|", written)).IsEqualTo("A:True|B:False|A:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 4, SuccessCount: 2, FailureCount: 2));
    }

    /// <summary>
    /// 未キャッシュ A → キャッシュ済み B → 未キャッシュ C を流し、結果と channel に書かれたテキストを返す。
    /// </summary>
    private static async Task<(SynthesisResult Result, List<string> Written)> ProduceAsync(FakeEngineServer engine)
    {
        var (result, written) = await ProduceAsync(
            engine,
            [
                new TextSegment("A"),
                new TextSegment("B") { IsCached = true, AudioData = TestAudio.SineWav(24000, 100) },
                new TextSegment("C"),
            ]);
        return (result, written.Select(w => w.Split(':')[0]).ToList());
    }

    /// <summary>
    /// segments を流し、結果と channel に書かれた "テキスト:音声の有無" を返す。
    /// inspectCacheDirectory はキャッシュディレクトリを消す前に呼ぶ (保存は ProduceAsync の終了までに完了している)。
    /// </summary>
    private static async Task<(SynthesisResult Result, List<string> Written)> ProduceAsync(
        FakeEngineServer engine, List<TextSegment> segments, Action<string>? inspectCacheDirectory = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_producer_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var apiClient = engine.CreateApiClient();
            var cacheManager = new AudioCacheManager(
                new CacheSettings { Directory = dir, UseExecutableBaseDirectory = false }, NullLogger.Instance, SystemClock.Instance);
            var producer = new SegmentProducer(apiClient, cacheManager, NullLogger.Instance);
            var channel = Channel.CreateUnbounded<TextSegment>();

            var result = await producer.ProduceAsync(
                segments, channel.Writer, new VoiceRequest(speakerId: FakeEngineServer.SpeakerId), saveToCache: true, CancellationToken.None);

            var written = new List<string>();
            await foreach (var segment in channel.Reader.ReadAllAsync())
            {
                written.Add($"{segment.Text}:{segment.AudioData != null}");
            }
            inspectCacheDirectory?.Invoke(dir);
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

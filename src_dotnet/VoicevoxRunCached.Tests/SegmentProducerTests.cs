using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
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

    [Test]
    public async Task AllCached_DoesNotPrepareSynthesis()
    {
        // 全部キャッシュから読めればエンジンの準備 (起動確認・話者の初期化) をしない
        using var engine = new FakeEngineServer();
        var prepareCount = 0;

        var (result, written) = await ProduceAsync(
            engine,
            [Cached("A"), Cached("B")],
            prepareSynthesis: _ => { prepareCount++; return Task.CompletedTask; });

        await Assert.That(prepareCount).IsEqualTo(0);
        await Assert.That(String.Join("|", written)).IsEqualTo("A:True|B:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(0, 0, 0));
    }

    [Test]
    public async Task Synthesis_PreparesOnceBeforeFirstSynthesis()
    {
        using var engine = new FakeEngineServer();
        var prepareCount = 0;
        var queriesAtPrepare = -1;

        var (result, _) = await ProduceAsync(
            engine,
            [Cached("A"), new TextSegment("B"), new TextSegment("C")],
            prepareSynthesis: _ =>
            {
                prepareCount++;
                queriesAtPrepare = engine.AudioQueryRequestCount;
                return Task.CompletedTask;
            });

        await Assert.That(prepareCount).IsEqualTo(1);
        await Assert.That(queriesAtPrepare).IsEqualTo(0);
        await Assert.That(result).IsEqualTo(new SynthesisResult(2, 2, 0));
    }

    [Test]
    public async Task PrepareFailure_IsThrown()
    {
        // エンジンが無い・話者が無い等の準備の失敗は文ごとの失敗にせず、そのまま投げる (終了コード 3 / 1)
        using var engine = new FakeEngineServer();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProduceAsync(
            engine,
            [new TextSegment("A")],
            prepareSynthesis: _ => throw new InvalidOperationException("engine")));
    }

    [Test]
    public async Task UnreadableCache_IsResynthesized()
    {
        // 読めないキャッシュは削除され、合成し直して流す
        using var engine = new FakeEngineServer();

        var (result, written) = await ProduceAsync(engine, [new TextSegment("A") { IsCached = true, AudioData = [1, 2, 3, 4] }]);

        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(String.Join("|", written)).IsEqualTo("A:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(1, 1, 0));
    }

    [Test]
    public async Task UnreadableCache_WithoutSynthesis_CountsAsFailure()
    {
        // --cache-only (prepareSynthesis = null) では合成せず、失敗として数える
        using var engine = new FakeEngineServer();

        var (result, written) = await ProduceAsync(
            engine,
            [Cached("A"), new TextSegment("B") { IsCached = true, AudioData = [1, 2, 3, 4] }],
            allowSynthesis: false);

        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(0);
        await Assert.That(String.Join("|", written)).IsEqualTo("A:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(1, 0, 1));
    }

    [Test]
    public async Task DuplicateUnreadableCache_IsDeletedOnceAndResynthesizedOnce()
    {
        // 回帰: 2 回目もデコードし直して同じキーを削除し、1 回目に合成し直した音声の保存と重なりえた
        using var engine = new FakeEngineServer();
        var cacheLogger = new WarningCountingLogger();
        string[] cachedFiles = [];

        var (result, written) = await ProduceAsync(
            engine,
            [
                new TextSegment("A") { IsCached = true, AudioData = [1, 2, 3, 4] },
                new TextSegment("A") { IsCached = true, AudioData = [1, 2, 3, 4] },
            ],
            dir => cachedFiles = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToArray(),
            cacheLogger: cacheLogger);

        await Assert.That(cacheLogger.WarningCount).IsEqualTo(1);
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(String.Join("|", written)).IsEqualTo("A:True|A:True");
        await Assert.That(result).IsEqualTo(new SynthesisResult(2, 2, 0));
        // 合成し直した音声が meta.json と対で残る
        await Assert.That(cachedFiles.Count(f => f.EndsWith(".opus", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(cachedFiles.Count(f => f.EndsWith(".meta.json", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task DuplicateCachedText_IsDecodedOnce()
    {
        // 2 回目以降はデコードし直さず 1 回目の音声を使う (読めないキャッシュなら Warning は 1 回だけ)
        using var engine = new FakeEngineServer();
        var cacheLogger = new WarningCountingLogger();

        var (result, written) = await ProduceAsync(
            engine,
            [Cached("A"), Cached("A"), new TextSegment("B") { IsCached = true, AudioData = [1, 2, 3, 4] }, new TextSegment("B") { IsCached = true, AudioData = [1, 2, 3, 4] }],
            cacheLogger: cacheLogger,
            allowSynthesis: false);

        await Assert.That(String.Join("|", written)).IsEqualTo("A:True|A:True");
        await Assert.That(cacheLogger.WarningCount).IsEqualTo(1);
        await Assert.That(result).IsEqualTo(new SynthesisResult(2, 0, 2));
    }

    private sealed class WarningCountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private int _warningCount;
        public int WarningCount => _warningCount;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // デコードは thread pool で行われる
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                Interlocked.Increment(ref _warningCount);
        }
    }

    private static TextSegment Cached(string text)
        => new(text) { IsCached = true, AudioData = OpusCodec.EncodeWav(TestAudio.SineWav(24000, 100)) };

    /// <summary>
    /// 未キャッシュ A → キャッシュ済み B → 未キャッシュ C を流し、結果と channel に書かれたテキストを返す。
    /// </summary>
    private static async Task<(SynthesisResult Result, List<string> Written)> ProduceAsync(FakeEngineServer engine)
    {
        var (result, written) = await ProduceAsync(engine, [new TextSegment("A"), Cached("B"), new TextSegment("C")]);
        return (result, written.Select(w => w.Split(':')[0]).ToList());
    }

    /// <summary>
    /// segments を流し、結果と channel に書かれた "テキスト:音声の有無" を返す。
    /// inspectCacheDirectory はキャッシュディレクトリを消す前に呼ぶ (保存は ProduceAsync の終了までに完了している)。
    /// </summary>
    /// <param name="prepareSynthesis">合成の準備。省略時は何もしない</param>
    /// <param name="allowSynthesis">false なら合成しない (<c>--cache-only</c>)</param>
    /// <param name="cacheLogger">AudioCacheManager に渡すロガー。省略時は NullLogger</param>
    private static async Task<(SynthesisResult Result, List<string> Written)> ProduceAsync(
        FakeEngineServer engine,
        List<TextSegment> segments,
        Action<string>? inspectCacheDirectory = null,
        Func<CancellationToken, Task>? prepareSynthesis = null,
        bool allowSynthesis = true,
        Microsoft.Extensions.Logging.ILogger? cacheLogger = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_producer_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var apiClient = engine.CreateApiClient();
            var cacheManager = new AudioCacheManager(
                new CacheSettings { Directory = dir, UseExecutableBaseDirectory = false }, cacheLogger ?? NullLogger.Instance, SystemClock.Instance);
            var producer = new SegmentProducer(apiClient, cacheManager, NullLogger.Instance);
            var channel = Channel.CreateUnbounded<TextSegment>();

            var result = await producer.ProduceAsync(
                segments,
                channel.Writer,
                new VoiceRequest(speakerId: FakeEngineServer.SpeakerId),
                saveToCache: true,
                allowSynthesis ? prepareSynthesis ?? (_ => Task.CompletedTask) : null,
                CancellationToken.None);

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

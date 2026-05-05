using System.Threading.Channels;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// TextToSpeechProcessor.ProduceSegmentsAsync で採用している順序保証
/// (writer.TryComplete() は pendingCacheSaves の WhenAll より先) を契約レベルでテストする。
/// 本実装が誤って WhenAll を先に await するとフィラー誤発火が再発するため、
/// この順序が逆転していないかをユニットテストで早期に検出する。
/// </summary>
public class ChannelProducerOrderingTests
{
    [Test]
    public async Task WriterCompletes_BeforeCacheSavesFinish()
    {
        var channel = Channel.CreateUnbounded<int>();
        var saveCompletion = new TaskCompletionSource<bool>();

        // 「キャッシュ保存」を擬似する遅延タスク。saveCompletion を上位から制御する。
        Task Save() => saveCompletion.Task;

        var producerTask = ProduceCoreAsync(channel.Writer, Save);

        // consumer 側 — channel が閉じるまで読む
        var readCount = 0;
        await foreach (var _ in channel.Reader.ReadAllAsync())
        {
            readCount++;
        }

        // この時点で channel はもう閉じている (= writer.Complete された)
        // しかし producer 全体はまだ saveCompletion 待ちで終わっていない
        await Assert.That(readCount).IsEqualTo(3);
        await Assert.That(producerTask.IsCompleted).IsFalse();

        // ここでキャッシュ保存を解放
        saveCompletion.SetResult(true);
        await producerTask;

        await Assert.That(producerTask.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// 本番側 ProduceSegmentsAsync と同じ順序: write 群 → finally で TryComplete → 後処理 await
    /// </summary>
    private static async Task ProduceCoreAsync(ChannelWriter<int> writer, Func<Task> saveTaskFactory)
    {
        var pendingSaves = new List<Task>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                await writer.WriteAsync(i);
                pendingSaves.Add(saveTaskFactory());
            }
        }
        finally
        {
            writer.TryComplete();
        }

        await Task.WhenAll(pendingSaves);
    }
}

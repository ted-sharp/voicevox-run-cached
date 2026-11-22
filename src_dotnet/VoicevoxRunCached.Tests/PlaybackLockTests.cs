using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 再生デバイスごとのロック。排他はハンドル単位なので、同一プロセス内の 2 回の取得でもプロセス間と同じ振る舞いになる。
/// </summary>
public class PlaybackLockTests
{
    [Test]
    public async Task LockFilePath_IsPerDevice()
    {
        var playbackLock = new PlaybackLock(NullLogger.Instance) { LockDirectory = @"C:\locks" };

        await Assert.That(playbackLock.LockFilePath(-1)).IsEqualTo(@"C:\locks\VoicevoxRunCached.playback.default.lock");
        await Assert.That(playbackLock.LockFilePath(0)).IsEqualTo(@"C:\locks\VoicevoxRunCached.playback.0.lock");
        await Assert.That(playbackLock.LockFilePath(2)).IsNotEqualTo(playbackLock.LockFilePath(0));
    }

    [Test]
    public async Task SameDevice_Waits_OtherDevice_DoesNot()
    {
        using var dir = new TempLockDirectory();
        var playbackLock = dir.CreatePlaybackLock();
        using var held = await playbackLock.AcquireAsync(1, maxWait: null, TimeSpan.Zero, CancellationToken.None);

        var sameDevice = playbackLock.AcquireAsync(1, maxWait: null, TimeSpan.Zero, CancellationToken.None);
        using var otherDevice = await playbackLock.AcquireAsync(2, TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);
        await Task.Delay(300);
        await Assert.That(sameDevice.IsCompleted).IsFalse();

        held.Dispose();
        using var acquired = await sameDevice.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(acquired).IsNotNull();
    }

    [Test]
    public async Task MaxWait_CountsTimeAlreadyWaitedForSpeechLock()
    {
        // --max-wait は 2 つのロックを待った時間の合計に掛ける。SpeechLock で上限まで待ったら、再生のロックは待たない。
        // エラーには残りの秒数でなく、指定した上限を出す。合成は済んでいるので「実行しませんでした」でなく「再生しませんでした」
        using var dir = new TempLockDirectory();
        var playbackLock = dir.CreatePlaybackLock();
        using var held = await playbackLock.AcquireAsync(1, maxWait: null, TimeSpan.Zero, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            playbackLock.AcquireAsync(1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Lock.LockWaitTimeout);
        await Assert.That(ex.UserMessage).Contains("30 秒");
        await Assert.That(ex.UserMessage).Contains("再生しませんでした");
    }

    [Test]
    public async Task MaxWaitAlreadySpent_FreeDevice_IsAcquired()
    {
        using var dir = new TempLockDirectory();
        var playbackLock = dir.CreatePlaybackLock();

        using var acquired = await playbackLock.AcquireAsync(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(acquired).IsNotNull();
    }
}

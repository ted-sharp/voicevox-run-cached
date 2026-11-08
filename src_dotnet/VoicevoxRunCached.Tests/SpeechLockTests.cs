using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class SpeechLockTests
{
    [Test]
    [Arguments(3, 10, 3)]
    [Arguments(0, 10, 0)]
    [Arguments(null, 10, 10)]
    [Arguments(null, null, null)]
    public async Task ResolveMaxWait_CommandLineOverridesSettings(int? commandLine, int? setting, int? expectedSeconds)
    {
        using var engineManager = new VoiceVoxEngineManager(new VoiceVoxSettings(), NullLogger.Instance, SystemClock.Instance);
        var speechLock = new SpeechLock(engineManager, new LockSettings { MaxWaitSeconds = setting }, NullLogger.Instance);

        var resolved = speechLock.ResolveMaxWait(commandLine);

        await Assert.That(resolved).IsEqualTo(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
    }

    [Test]
    public async Task RunAsync_GivesUpWithoutRunningWhenLockIsHeld()
    {
        using var engineManager = new VoiceVoxEngineManager(new VoiceVoxSettings(), NullLogger.Instance, SystemClock.Instance);
        var speechLock = new SpeechLock(engineManager, new LockSettings { MaxWaitSeconds = 0 }, NullLogger.Instance);
        var ran = false;

        using (await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, NullLogger.Instance))
        {
            var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() => speechLock.RunAsync(() =>
            {
                ran = true;
                return Task.FromResult(0);
            }).WaitAsync(TimeSpan.FromSeconds(10)));

            await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.Lock.LockWaitTimeout);
        }

        await Assert.That(ran).IsFalse();
    }
}

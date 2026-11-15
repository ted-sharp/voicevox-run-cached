using System.Diagnostics;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class ProcessInstancesTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    [Test]
    public async Task SameExecutableRunning_IsAnotherInstance()
    {
        using var process = StartPing();
        try
        {
            await Assert.That(ProcessInstances.IsAnotherInstanceRunning(Ping, excludedProcessId: -1)).IsTrue();
        }
        finally
        {
            process.Kill();
        }
    }

    [Test]
    public async Task SameNameInOtherFolder_IsNotAnotherInstance()
    {
        // 回帰: 名前だけで比べていたため、VOICEVOX のエンジン (run.exe) が落ちても、ほかの run.exe (AivisSpeech など) が動いていると
        // 起動中のインスタンスとみなし、タイムアウトまで待っていた
        using var process = StartPing();
        try
        {
            var otherPath = Path.Combine(Path.GetTempPath(), "vrc_" + Guid.NewGuid().ToString("N"), "PING.EXE");

            await Assert.That(ProcessInstances.IsAnotherInstanceRunning(otherPath, excludedProcessId: -1)).IsFalse();
        }
        finally
        {
            process.Kill();
        }
    }

    private static Process StartPing()
        => Process.Start(new ProcessStartInfo(Ping, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
}

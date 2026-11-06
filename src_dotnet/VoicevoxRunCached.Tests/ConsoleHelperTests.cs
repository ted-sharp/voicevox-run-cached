using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// ConsoleHelper の出力先。エラー・警告とその補足は stderr、結果・進捗は stdout。
/// </summary>
/// <remarks>
/// 回帰: すべて stdout に出ていたため、Claude Code の Hooks (失敗時は stderr を表示する) で失敗理由が見えず、
/// <c>devices --json</c> の stdout にも余計な行が混ざっていた。
/// 出力は TUnit がテストごとに stdout / stderr を分けて捕まえたものを見る。
/// </remarks>
public class ConsoleHelperTests
{
    [Test]
    public async Task ErrorsAndWarnings_GoToStderr()
    {
        ConsoleHelper.WriteError("err-message");
        ConsoleHelper.WriteWarning("warn-message");
        ConsoleHelper.WriteErrorDetail("detail-message");

        var stderr = TestContext.Current!.GetErrorOutput();
        await Assert.That(stderr).Contains("err-message");
        await Assert.That(stderr).Contains("warn-message");
        await Assert.That(stderr).Contains("detail-message");
        await Assert.That(TestContext.Current.GetStandardOutput()).DoesNotContain("message");
    }

    [Test]
    public async Task ResultsAndProgress_GoToStdout()
    {
        ConsoleHelper.WriteSuccess("success-message");
        ConsoleHelper.WriteLine("line-message");

        var stdout = TestContext.Current!.GetStandardOutput();
        await Assert.That(stdout).Contains("success-message");
        await Assert.That(stdout).Contains("line-message");
        await Assert.That(TestContext.Current.GetErrorOutput()).DoesNotContain("message");
    }
}

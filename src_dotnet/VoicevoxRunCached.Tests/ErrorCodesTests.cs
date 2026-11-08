using System.Reflection;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Tests;

public class ErrorCodesTests
{
    [Test]
    [Arguments(null, 1)]
    [Arguments(ErrorCodes.General.InvalidArguments, 1)]
    [Arguments(ErrorCodes.Engine.EngineNotAvailable, 3)]
    [Arguments(ErrorCodes.Cache.CacheReadError, 4)]
    [Arguments(ErrorCodes.Audio.AudioGenerationFailed, 5)]
    [Arguments(ErrorCodes.Api.ApiTimeout, 6)]
    [Arguments(ErrorCodes.Configuration.InvalidSettings, 7)]
    [Arguments(ErrorCodes.Lock.LockWaitTimeout, 9)]
    public async Task GetExitCode_MapsCategory(string? errorCode, int expected)
    {
        await Assert.That(ErrorCodes.GetExitCode(errorCode)).IsEqualTo(expected);
    }

    [Test]
    public async Task ExitCodes_NeverUseTwo()
    {
        // Claude Code の Hooks では終了コード 2 がブロッキングエラー (Stop フックなら停止を阻止) になる。
        // 後から追加したコードも漏れなく検査するよう、定数はリフレクションで列挙する
        var errorCodes = typeof(ErrorCodes).GetNestedTypes()
            .SelectMany(GetConstants<string>)
            .ToList();
        var directExitCodes = GetConstants<int>(typeof(ErrorCodes)).ToList(); // CancelledExitCode など

        await Assert.That(errorCodes).Contains(ErrorCodes.Configuration.InvalidSettings); // 列挙できていること
        await Assert.That(directExitCodes).Contains(ErrorCodes.PartialFailureExitCode);
        await Assert.That(errorCodes.Select(ErrorCodes.GetExitCode).Concat(directExitCodes)).DoesNotContain(2);
    }

    private static IEnumerable<T> GetConstants<T>(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(T))
        .Select(f => (T)f.GetRawConstantValue()!);
}

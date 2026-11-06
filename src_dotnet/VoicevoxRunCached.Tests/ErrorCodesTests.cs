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
    public async Task GetExitCode_MapsCategory(string? errorCode, int expected)
    {
        await Assert.That(ErrorCodes.GetExitCode(errorCode)).IsEqualTo(expected);
    }

    [Test]
    public async Task ExitCodes_NeverUseTwo()
    {
        // Claude Code の Hooks では終了コード 2 がブロッキングエラー (Stop フックなら停止を阻止) になる
        string[] allCodes =
        [
            ErrorCodes.Configuration.InvalidSettings,
            ErrorCodes.Engine.EngineNotAvailable, ErrorCodes.Engine.EngineProcessError,
            ErrorCodes.Cache.CacheReadError, ErrorCodes.Cache.CachePermissionDenied,
            ErrorCodes.Audio.AudioGenerationFailed, ErrorCodes.Audio.AudioFormatError, ErrorCodes.Audio.AudioFileExportFailed,
            ErrorCodes.Api.ApiRequestFailed, ErrorCodes.Api.ApiResponseInvalid, ErrorCodes.Api.ApiTimeout,
            ErrorCodes.Api.ApiAuthenticationError, ErrorCodes.Api.ApiRateLimitExceeded,
            ErrorCodes.General.UnknownError, ErrorCodes.General.InvalidArguments,
            ErrorCodes.General.ResourceNotFound, ErrorCodes.General.PermissionDenied,
        ];

        var exitCodes = allCodes.Select(ErrorCodes.GetExitCode)
            .Append(ErrorCodes.PartialFailureExitCode)
            .Append(ErrorCodes.CancelledExitCode);

        await Assert.That(exitCodes).DoesNotContain(2);
    }
}

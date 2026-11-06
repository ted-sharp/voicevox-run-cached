using System.Net;
using System.Text.Json;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VoiceVoxApiClient.MapToErrorCode: リトライ後に残った例外 → エラーコード (→ 終了コード) の対応。
/// </summary>
public class VoiceVoxApiClientErrorMappingTests
{
    [Test]
    public async Task ConnectionFailure_MapsToEngineNotAvailable()
    {
        // 回帰: ステータスの無い HttpRequestException (接続拒否など) が「不明なエラー」(終了コード 1) になっていた
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made");

        var code = VoiceVoxApiClient.MapToErrorCode(ex);

        await Assert.That(code).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
        await Assert.That(ErrorCodes.GetExitCode(code)).IsEqualTo(3);
    }

    [Test]
    public async Task ResponseEnded_MapsToEngineNotAvailable()
    {
        // 応答の読み取り中に接続が切れると HttpIOException (IOException の派生) になる
        var ex = new HttpIOException(HttpRequestError.ResponseEnded);

        await Assert.That(VoiceVoxApiClient.MapToErrorCode(ex)).IsEqualTo(ErrorCodes.Engine.EngineNotAvailable);
    }

    [Test]
    [Arguments(HttpStatusCode.InternalServerError, ErrorCodes.Engine.EngineProcessError)]
    [Arguments(HttpStatusCode.ServiceUnavailable, ErrorCodes.Engine.EngineNotAvailable)]
    [Arguments(HttpStatusCode.TooManyRequests, ErrorCodes.Api.ApiRateLimitExceeded)]
    [Arguments(HttpStatusCode.BadRequest, ErrorCodes.Api.ApiRequestFailed)]
    public async Task HttpStatus_MapsByStatusCode(HttpStatusCode status, string expected)
    {
        var ex = new HttpRequestException("error", null, status);

        await Assert.That(VoiceVoxApiClient.MapToErrorCode(ex)).IsEqualTo(expected);
    }

    [Test]
    public async Task OtherExceptions_MapByType()
    {
        await Assert.That(VoiceVoxApiClient.MapToErrorCode(new TaskCanceledException())).IsEqualTo(ErrorCodes.Api.ApiTimeout);
        await Assert.That(VoiceVoxApiClient.MapToErrorCode(new JsonException())).IsEqualTo(ErrorCodes.Api.ApiResponseInvalid);
        await Assert.That(VoiceVoxApiClient.MapToErrorCode(new InvalidOperationException())).IsEqualTo(ErrorCodes.General.UnknownError);
    }
}

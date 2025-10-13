using System.Net;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// エラーハンドリングの動作を確認するためのテスト用サンプルコード
/// 実際のテストプロジェクトでは、xUnitやNUnitなどのテストフレームワークを使用してください
/// </summary>
public static class ErrorHandlingTests
{
    /// <summary>
    /// HTTPステータスコードからエラーコードへの変換をテストします
    /// </summary>
    public static void TestHttpStatusToErrorCode()
    {
        Console.WriteLine("=== HTTPステータスコードからエラーコードへの変換テスト ===");

        var testCases = new[]
        {
            HttpStatusCode.BadRequest,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout,
            HttpStatusCode.RequestTimeout
        };

        foreach (var statusCode in testCases)
        {
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromHttpStatus(statusCode);
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromHttpStatus(statusCode);
            var suggestedSolution = ErrorHandlingUtility.GetSuggestedSolutionFromHttpStatus(statusCode);

            Console.WriteLine($"Status: {statusCode} -> ErrorCode: {errorCode}");
            Console.WriteLine($"  UserMessage: {userMessage}");
            Console.WriteLine($"  SuggestedSolution: {suggestedSolution}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// 例外からエラーコードへの変換をテストします
    /// </summary>
    public static void TestExceptionToErrorCode()
    {
        Console.WriteLine("=== 例外からエラーコードへの変換テスト ===");

        var testExceptions = new Exception[]
        {
            new UnauthorizedAccessException("アクセス権限がありません"),
            new IOException("ファイルが見つかりません"),
            new ArgumentException("無効な引数です"),
            new InvalidOperationException("操作が無効です"),
            new OperationCanceledException("操作がキャンセルされました"),
            new TaskCanceledException("タスクがタイムアウトしました"),
            new TimeoutException("タイムアウトが発生しました")
        };

        foreach (var exception in testExceptions)
        {
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(exception);
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(exception);
            var suggestedSolution = ErrorHandlingUtility.GetSuggestedSolutionFromException(exception);

            Console.WriteLine($"Exception: {exception.GetType().Name} -> ErrorCode: {errorCode}");
            Console.WriteLine($"  UserMessage: {userMessage}");
            Console.WriteLine($"  SuggestedSolution: {suggestedSolution}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// カスタム例外の作成と使用をテストします
    /// </summary>
    public static void TestCustomExceptions()
    {
        Console.WriteLine("=== カスタム例外のテスト ===");

        try
        {
            // VoiceVoxApiExceptionのテスト
            throw new VoiceVoxApiException(
                ErrorCodes.Engine.EngineNotAvailable,
                "VOICEVOX engine is not running",
                "VOICEVOXエンジンが起動していません。",
                HttpStatusCode.ServiceUnavailable,
                null,
                "VOICEVOXエンジンを起動してください。"
            );
        }
        catch (VoiceVoxApiException ex)
        {
            Console.WriteLine($"VoiceVoxApiException caught:");
            Console.WriteLine($"  ErrorCode: {ex.ErrorCode}");
            Console.WriteLine($"  UserMessage: {ex.UserMessage}");
            Console.WriteLine($"  SuggestedSolution: {ex.SuggestedSolution}");
            Console.WriteLine($"  StatusCode: {ex.StatusCode}");
            Console.WriteLine($"  JSON: {ex.ToJson()}");
            Console.WriteLine();
        }

        try
        {
            // CacheExceptionのテスト
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                "Access denied to cache directory",
                "キャッシュディレクトリへのアクセスが拒否されました。",
                "test-cache-key",
                "./cache/",
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (CacheException ex)
        {
            Console.WriteLine($"CacheException caught:");
            Console.WriteLine($"  ErrorCode: {ex.ErrorCode}");
            Console.WriteLine($"  UserMessage: {ex.UserMessage}");
            Console.WriteLine($"  SuggestedSolution: {ex.SuggestedSolution}");
            Console.WriteLine($"  CacheKey: {ex.CacheKey}");
            Console.WriteLine($"  CachePath: {ex.CachePath}");
            Console.WriteLine($"  JSON: {ex.ToJson()}");
            Console.WriteLine();
        }

        try
        {
            // ConfigurationExceptionのテスト
            throw new ConfigurationException(
                ErrorCodes.Configuration.InvalidSettings,
                "Invalid configuration settings",
                "設定が無効です。",
                "VoiceVox.BaseUrl",
                "設定ファイルの内容を確認してください。"
            );
        }
        catch (ConfigurationException ex)
        {
            Console.WriteLine($"ConfigurationException caught:");
            Console.WriteLine($"  ErrorCode: {ex.ErrorCode}");
            Console.WriteLine($"  UserMessage: {ex.UserMessage}");
            Console.WriteLine($"  SuggestedSolution: {ex.SuggestedSolution}");
            Console.WriteLine($"  SettingPath: {ex.SettingPath}");
            Console.WriteLine($"  JSON: {ex.ToJson()}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// エラーコードから終了コードへの変換をテストします
    /// </summary>
    public static void TestErrorCodeToExitCode()
    {
        Console.WriteLine("=== エラーコードから終了コードへの変換テスト ===");

        var testErrorCodes = new[]
        {
            ErrorCodes.Configuration.InvalidSettings,
            ErrorCodes.Engine.EngineNotAvailable,
            ErrorCodes.Cache.CacheReadError,
            ErrorCodes.Audio.AudioGenerationFailed,
            ErrorCodes.Api.ApiRequestFailed,
            ErrorCodes.General.UnknownError
        };

        foreach (var errorCode in testErrorCodes)
        {
            var exitCode = ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
            var category = ErrorCodes.GetCategory(errorCode);
            var description = ErrorCodes.GetDescription(errorCode);

            Console.WriteLine($"ErrorCode: {errorCode} -> ExitCode: {exitCode}");
            Console.WriteLine($"  Category: {category}");
            Console.WriteLine($"  Description: {description}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// 全テストを実行します
    /// </summary>
    public static void RunAllTests()
    {
        Console.WriteLine("エラーハンドリングテストを開始します...\n");

        TestHttpStatusToErrorCode();
        TestExceptionToErrorCode();
        TestCustomExceptions();
        TestErrorCodeToExitCode();

        Console.WriteLine("エラーハンドリングテストが完了しました。");
    }
}

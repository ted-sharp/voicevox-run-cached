using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached;

/// <summary>
/// VoicevoxRunCached アプリケーションのメインエントリーポイント
/// </summary>
static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            // アプリケーションの初期化
            var (settings, logger) = await ApplicationBootstrap.InitializeAsync(args);

            // コマンドルーターを作成してコマンドを実行
            using var cancellationManager = new CancellationManager(logger);
            var commandRouter = new CommandRouter(settings, logger);

            var result = await commandRouter.ExecuteCommandAsync(args, cancellationManager.Token);

            // Serilogのクリーンアップ
            ProgramExtensions.CleanupSerilog();

            return result;
        }
        catch (VoicevoxRunCachedException ex)
        {
            // カスタム例外は適切に処理
            Console.WriteLine($"\e[31mFatal Error: {ex.UserMessage}\e[0m");

            if (!String.IsNullOrEmpty(ex.SuggestedSolution))
            {
                Console.WriteLine($"\e[33m解決策: {ex.SuggestedSolution}\e[0m");
            }

            if (!String.IsNullOrEmpty(ex.Context))
            {
                Console.WriteLine($"\e[36mコンテキスト: {ex.Context}\e[0m");
            }

            Console.WriteLine($"\e[90mエラーコード: {ex.ErrorCode}\e[0m");
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\e[33mアプリケーションがキャンセルされました\e[0m");
            return 0;
        }
        catch (Exception ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            Console.WriteLine($"\e[31mFatal Error: {userMessage} - {ex.Message}\e[0m");
            Console.WriteLine($"\e[33m{solution}\e[0m");
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
    }
}

// Serilogの適切なクローズ処理のための拡張メソッド
public static class ProgramExtensions
{
    public static void CleanupSerilog()
    {
        try
        {
            // Serilogの CloseAndFlush を直接呼び出す
            // 通常は瞬時に完了し、内部でタイムアウト処理も実装されている
            Serilog.Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            // クリーンアップが失敗しても致命的ではない
            Console.WriteLine($"Warning: Failed to cleanup Serilog: {ex.Message}");
        }
    }
}

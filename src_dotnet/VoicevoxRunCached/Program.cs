using DryIoc;
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
            // アプリケーションの初期化 (設定ロード + Serilog 設定 + MediaFoundation 初期化)
            var (settings, logger) = await ApplicationBootstrap.InitializeAsync(args);

            // DI コンテナ構築
            using var container = ServiceRegistration.Build(settings, logger);

            using var cancellationManager = new CancellationManager(logger);
            var commandRouter = container.Resolve<CommandRouter>();

            var result = await commandRouter.ExecuteCommandAsync(args, cancellationManager.Token);

            ProgramExtensions.CleanupSerilog();

            return result;
        }
        catch (VoicevoxRunCachedException ex)
        {
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
            return ErrorHandlingUtility.HandleExceptionAndGetExitCode(ex);
        }
    }
}

public static class ProgramExtensions
{
    public static void CleanupSerilog()
    {
        try
        {
            Serilog.Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to cleanup Serilog: {ex.Message}");
        }
    }
}

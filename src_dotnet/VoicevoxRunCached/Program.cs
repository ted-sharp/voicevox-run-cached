using Microsoft.Extensions.Logging;
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
            // アプリケーションの初期化 (設定ロード + Serilog 設定)
            var (settings, logger) = await ApplicationBootstrap.InitializeAsync(args);

            // DI コンテナ構築 (Jab のコンパイル時生成プロバイダ)
            using var services = new AppServiceProvider { Settings = settings, Logger = logger };

            using var cancellationManager = new CancellationManager(logger);
            var commandRouter = services.GetService<CommandRouter>();

            try
            {
                var result = await commandRouter.ExecuteCommandAsync(args, cancellationManager.Token);
                return result;
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
            }
        }
        catch (VoicevoxRunCachedException ex)
        {
            ConsoleHelper.WriteError($"Fatal Error: {ex.UserMessage}");

            if (!String.IsNullOrEmpty(ex.SuggestedSolution))
            {
                ConsoleHelper.WriteWarning($"解決策: {ex.SuggestedSolution}");
            }

            if (!String.IsNullOrEmpty(ex.Context))
            {
                ConsoleHelper.WriteInfo($"コンテキスト: {ex.Context}");
            }

            ConsoleHelper.WriteDebug($"エラーコード: {ex.ErrorCode}");
            return ErrorCodes.GetExitCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteWarning("アプリケーションがキャンセルされました");
            return 0;
        }
        catch (Exception ex)
        {
            return ErrorHandlingUtility.HandleExceptionAndGetExitCode(ex);
        }
    }

}

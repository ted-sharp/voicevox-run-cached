using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
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

            try
            {
                return await RunAsync(args, settings, logger);
            }
            finally
            {
                // RunAsync を抜けた時点でサービスの Dispose (エンジン停止など) は完了している。
                // その間のログを失わないよう、Serilog はここで閉じる
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
            return ErrorCodes.CancelledExitCode;
        }
        catch (Exception ex)
        {
            return ErrorHandlingUtility.HandleExceptionAndGetExitCode(ex);
        }
    }

    /// <summary>
    /// DI コンテナを構築してコマンドを実行する。コンテナはこのメソッドを抜けるときに破棄される。
    /// </summary>
    private static async Task<int> RunAsync(string[] args, AppSettings settings, ILogger logger)
    {
        // DI コンテナ構築 (Jab のコンパイル時生成プロバイダ)
        using var services = new AppServiceProvider { Settings = settings, Logger = logger };
        using var cancellationManager = new CancellationManager(logger);

        var commandRouter = services.GetService<CommandRouter>();
        return await commandRouter.ExecuteCommandAsync(args, cancellationManager.Token);
    }
}

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
                ConsoleHelper.WriteErrorDetail($"解決策: {ex.SuggestedSolution}");
            }

            if (!String.IsNullOrEmpty(ex.Context))
            {
                ConsoleHelper.WriteErrorDetail($"コンテキスト: {ex.Context}");
            }

            ConsoleHelper.WriteErrorDetail($"エラーコード: {ex.ErrorCode}");
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
        // CancellationManager は services より先に作り、後に破棄する。
        // 終了処理 (エンジン停止の待機など) の間に押された 1 回目の Ctrl+C も受け止め、プロセスを途中で落とさない
        // (2 回目は強制終了させる)
        using var cancellationManager = new CancellationManager(logger);

        // DI コンテナ構築 (Jab のコンパイル時生成プロバイダ)
        using var services = new AppServiceProvider { Settings = settings, Logger = logger };

        var commandRouter = services.GetService<CommandRouter>();
        return await commandRouter.ExecuteCommandAsync(args, cancellationManager.Token);
    }
}

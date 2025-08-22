using VoicevoxRunCached.Services;

namespace VoicevoxRunCached;

/// <summary>
/// VoicevoxRunCached アプリケーションのメインエントリーポイント
/// </summary>
class Program
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
        catch (Exception ex)
        {
            Console.WriteLine($"\e[31mFatal Error: {ex.Message}\e[0m");
            return 1;
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
            Serilog.Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to cleanup Serilog: {ex.Message}");
        }
    }
}

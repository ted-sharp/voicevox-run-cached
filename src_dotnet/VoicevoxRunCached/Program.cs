using VoicevoxRunCached.Exceptions;
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
            return GetExitCodeFromErrorCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\e[33mアプリケーションがキャンセルされました\e[0m");
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"\e[31mFatal Error: アクセス権限がありません - {ex.Message}\e[0m");
            Console.WriteLine("\e[33m管理者権限で実行するか、必要な権限を確認してください。\e[0m");
            return 1;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"\e[31mFatal Error: ファイルまたはディレクトリの操作に失敗しました - {ex.Message}\e[0m");
            Console.WriteLine("\e[33mファイルの存在とアクセス権限を確認してください。\e[0m");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\e[31mFatal Error: 予期しないエラーが発生しました - {ex.Message}\e[0m");
            Console.WriteLine("\e[33mアプリケーションを再起動し、問題が続く場合はログを確認してください。\e[0m");
            return 1;
        }
    }

    /// <summary>
    /// エラーコードから終了コードを取得します
    /// </summary>
    private static int GetExitCodeFromErrorCode(string errorCode)
    {
        if (String.IsNullOrEmpty(errorCode))
            return 1;

        var category = ErrorCodes.GetCategory(errorCode);
        return category switch
        {
            "Configuration" => 2,
            "Engine" => 3,
            "Cache" => 4,
            "Audio" => 5,
            "API" => 6,
            "General" => 1,
            _ => 1
        };
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

using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Commands;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// コマンドのルーティングと実行を行うクラス
/// </summary>
public class CommandRouter
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly CommandHandler _commandHandler;

    public CommandRouter(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this._commandHandler = new CommandHandler(settings, logger);
    }

    /// <summary>
    /// コマンドを実行します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>実行結果の終了コード</returns>
    public async Task<int> ExecuteCommandAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            ArgumentParser.ShowUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "speakers" => await this._commandHandler.HandleSpeakersAsync(),
                "devices" => this._commandHandler.HandleDevices(subArgs),
                "--init" => await this._commandHandler.HandleInitAsync(),
                "--clear" => await this._commandHandler.HandleClearCacheAsync(),
                "--benchmark" => await this._commandHandler.HandleBenchmarkAsync(),
                "--test" => await this.HandleTestCommandAsync(subArgs, cancellationToken),
                _ => await this.HandleTextToSpeechCommandAsync(args, cancellationToken)
            };
        }
        catch (VoicevoxRunCachedException ex)
        {
            // カスタム例外は適切に処理
            this._logger.LogError(ex, "コマンド実行中にエラーが発生しました: {Command}, ErrorCode: {ErrorCode}", command, ex.ErrorCode);
            ConsoleHelper.WriteError($"Error: {ex.UserMessage}", this._logger);

            if (!String.IsNullOrEmpty(ex.SuggestedSolution))
            {
                ConsoleHelper.WriteLine($"解決策: {ex.SuggestedSolution}", this._logger);
            }

            return this.GetExitCodeFromErrorCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            this._logger.LogInformation("コマンド実行がキャンセルされました: {Command}", command);
            ConsoleHelper.WriteLine("操作がキャンセルされました。", this._logger);
            return 0;
        }
        catch (ArgumentException ex)
        {
            this._logger.LogError(ex, "コマンドの引数が無効です: {Command}, Args: {Args}", command, String.Join(" ", subArgs));
            ConsoleHelper.WriteError($"Error: 無効な引数が指定されました - {ex.Message}", this._logger);
            ConsoleHelper.WriteLine("使用方法を確認するには --help オプションを使用してください。", this._logger);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            this._logger.LogError(ex, "コマンドの実行に必要な前提条件が満たされていません: {Command}", command);
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            this._logger.LogError(ex, "コマンド実行に必要な権限がありません: {Command}", command);
            ConsoleHelper.WriteError($"Error: アクセス権限がありません - {ex.Message}", this._logger);
            ConsoleHelper.WriteLine("管理者権限で実行するか、必要な権限を確認してください。", this._logger);
            return 1;
        }
        catch (IOException ex)
        {
            this._logger.LogError(ex, "ファイルまたはディレクトリの操作に失敗しました: {Command}", command);
            ConsoleHelper.WriteError($"Error: ファイル操作に失敗しました - {ex.Message}", this._logger);
            return 1;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "コマンド実行中に予期しないエラーが発生しました: {Command}", command);
            ConsoleHelper.WriteError($"Error: 予期しないエラーが発生しました - {ex.Message}", this._logger);
            ConsoleHelper.WriteLine("アプリケーションを再起動し、問題が続く場合はログを確認してください。", this._logger);
            return 1;
        }
    }

    /// <summary>
    /// エラーコードから終了コードを取得します
    /// </summary>
    private int GetExitCodeFromErrorCode(string errorCode)
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

    /// <summary>
    /// テストコマンドを処理します
    /// </summary>
    private async Task<int> HandleTestCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var testMessage = this._settings.Test?.Message ?? String.Empty;
        if (String.IsNullOrWhiteSpace(testMessage))
        {
            Console.WriteLine("\e[31mError: Test.Message is empty in configuration\e[0m");
            return 1;
        }

        this._logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
        ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}", this._logger);

        // 設定されたメッセージで最初の引数を置き換え
        var remaining = args.Where(arg => arg != null).Cast<string>().ToArray();
        var testArgs = new[] { testMessage }.Concat(remaining).ToArray();

        this._logger.LogInformation("Test args constructed: {TestArgs}", String.Join(" ", testArgs));
        ConsoleHelper.WriteLine($"Debug: Test args: {String.Join(" ", testArgs)}", this._logger);

        return await this.ExecuteTextToSpeechCommandAsync(testArgs, cancellationToken);
    }

    /// <summary>
    /// テキスト読み上げコマンドを処理します
    /// </summary>
    private async Task<int> HandleTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        return await this.ExecuteTextToSpeechCommandAsync(args, cancellationToken);
    }

    /// <summary>
    /// テキスト読み上げコマンドを実行します
    /// </summary>
    private async Task<int> ExecuteTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ArgumentParser.ParseArguments(args, this._settings);
        if (request == null)
        {
            ConsoleHelper.WriteError("Error: Invalid arguments", this._logger);
            ArgumentParser.ShowUsage();
            return 1;
        }

        var noCache = args.Contains("--no-cache");
        var cacheOnly = args.Contains("--cache-only");
        var verbose = args.Contains("--verbose");
        var outPath = args.FirstOrDefault(arg => arg.StartsWith("--out="))?.Substring("--out=".Length);
        var noPlay = args.Contains("--no-play");

        return await this._commandHandler.HandleTextToSpeechAsync(
            request, noCache, cacheOnly, verbose, outPath, noPlay, cancellationToken);
    }
}

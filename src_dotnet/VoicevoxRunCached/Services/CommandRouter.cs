using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Commands;

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
        catch (Exception ex)
        {
            this._logger.LogError(ex, "コマンド実行中にエラーが発生しました: {Command}", command);
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }

    /// <summary>
    /// テストコマンドを処理します
    /// </summary>
    private async Task<int> HandleTestCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var testMessage = this._settings.Test?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(testMessage))
        {
            Console.WriteLine("\e[31mError: Test.Message is empty in configuration\e[0m");
            return 1;
        }

        this._logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
        ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}", this._logger);

        // 設定されたメッセージで最初の引数を置き換え
        var remaining = args.Where(arg => arg != null).Cast<string>().ToArray();
        var testArgs = new[] { testMessage }.Concat(remaining).ToArray();

        this._logger.LogInformation("Test args constructed: {TestArgs}", string.Join(" ", testArgs));
        ConsoleHelper.WriteLine($"Debug: Test args: {string.Join(" ", testArgs)}", this._logger);

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
            Console.WriteLine($"\e[31mError: Invalid arguments\e[0m");
            ArgumentParser.ShowUsage();
            return 1;
        }

        string? outPath = ArgumentParser.GetStringOption(args, "--out") ?? ArgumentParser.GetStringOption(args, "-o");
        bool noPlay = ArgumentParser.GetBoolOption(args, "--no-play");

        return await this._commandHandler.HandleTextToSpeechAsync(
            request,
            ArgumentParser.GetBoolOption(args, "--no-cache"),
            ArgumentParser.GetBoolOption(args, "--cache-only"),
            ArgumentParser.GetBoolOption(args, "--verbose"),
            outPath,
            noPlay,
            cancellationToken);
    }
}

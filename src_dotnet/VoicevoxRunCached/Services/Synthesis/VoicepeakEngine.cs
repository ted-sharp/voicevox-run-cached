using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// VOICEPEAK のコマンドライン (voicepeak.exe) による <see cref="ISpeechEngine"/>。1 文ごとに exe を起動して WAV を出させる。
/// </summary>
/// <remarks>
/// <para>話者は <c>ナレーター名[:感情=値,...]</c> (例: <c>Otomachi Una:joy=50</c>)。感情で音声が変わるので、キャッシュキーに入る話者の文字列に含める。</para>
/// <para>voicepeak.exe は 1 回の起動に 10 秒ほどかかる (話者一覧も同じ)。存在しない話者・感情も約 10 秒待ってから同じエラー
/// (<c>bad exception</c>) で失敗し、どちらかは区別できない。そのため準備 (<see cref="PrepareAsync"/>) では exe の有無だけを見て、
/// 合成の失敗はすべて <see cref="IsEngineUnavailable"/> にする (話者を間違えると全部の文が 10 秒ずつ失敗し、その間ロックを持ち続けるため)。</para>
/// <para>1 回に渡せる本文は 140 文字まで (voicepeak.exe の制限)。<see cref="TextSegmentProcessor"/> の分割の上限 (100) より大きいので超えない。</para>
/// <para>話速・音高は CLI の値に換算する (<see cref="ToSpeed"/> / <see cref="ToPitch"/>)。音量は CLI に無いので、出力された WAV に掛ける。</para>
/// </remarks>
public class VoicepeakEngine : ISpeechEngine
{
    private readonly VoicepeakSettings _settings;
    private readonly ILogger _logger;

    public VoicepeakEngine(VoicepeakSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SpeechEngineKind Kind => SpeechEngineKind.Voicepeak;

    public IEngineFillerSettings FillerSettings => _settings;

    public Task PrepareAsync(string speaker, CancellationToken cancellationToken)
    {
        // 話者の存在確認 (--list-narrator) は 10 秒ほどかかるのでしない。存在しなければ最初の合成が失敗し、残りは合成しない
        ResolveExePathOrThrow();
        return Task.CompletedTask;
    }

    public async Task<byte[]> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exePath = ResolveExePathOrThrow();

        var outPath = Path.Combine(Path.GetTempPath(), $"vrc_voicepeak_{Guid.NewGuid():N}.wav");
        try
        {
            var result = await RunAsync(exePath, BuildSynthesisArguments(request, outPath), cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(outPath))
            {
                throw new VoicevoxRunCachedException(
                    errorCode: ErrorCodes.Engine.EngineProcessError,
                    message: $"voicepeak.exe failed (exit code {result.ExitCode}): {result.Output}",
                    userMessage: $"VOICEPEAK の合成に失敗しました (終了コード {result.ExitCode}): {result.Output}",
                    suggestedSolution: "話者名と感情名を `VoicevoxRunCached speakers --engine voicepeak` で確認してください。");
            }

            // 出力は WAV 48kHz mono 16bit で、fmt チャンクの前に JUNK チャンクがある (NAudio の WaveFileReader で読める)
            var wavData = await File.ReadAllBytesAsync(outPath, cancellationToken);
            // 音量は CLI に無いので WAV に掛ける
            var gain = CacheKeyGenerator.NormalizeParameter(request.Volume);
            return gain == 1.0 ? wavData : AudioConversionUtility.ApplyGain(wavData, (float)gain);
        }
        finally
        {
            TryDelete(outPath);
        }
    }

    public async Task<IReadOnlyList<SpeakerInfo>> GetSpeakersAsync(CancellationToken cancellationToken)
    {
        var exePath = ResolveExePathOrThrow();

        var narrators = ParseLines((await RunCheckedAsync(exePath, ["--list-narrator"], cancellationToken)).Output);
        var speakers = new List<SpeakerInfo>();
        foreach (var narrator in narrators)
        {
            var emotions = ParseLines((await RunCheckedAsync(exePath, ["--list-emotion", narrator], cancellationToken)).Output);
            var description = emotions.Count == 0
                ? ""
                : $"感情: {String.Join(", ", emotions)} (例: -s \"{narrator}:{emotions[0]}=50\")";
            speakers.Add(new SpeakerInfo(narrator, null, [new SpeakerVoice(narrator, description)]));
        }
        return speakers;
    }

    /// <remarks>
    /// voicepeak.exe の失敗はどれも、残りの文でも同じように失敗する見込みが高い (理由はクラスの remarks)。
    /// </remarks>
    public bool IsEngineUnavailable(Exception ex)
        => ex is VoicevoxRunCachedException
        {
            ErrorCode: ErrorCodes.Engine.EngineNotAvailable or ErrorCodes.Engine.EngineProcessError or ErrorCodes.Api.ApiTimeout
        };

    /// <summary>
    /// 何もしない (voicepeak.exe は 1 回ごとに終わり、常駐するものが無い)。
    /// </summary>
    public void StopIfNotKept()
    {
    }

    /// <summary>
    /// <c>--speaker</c> の値を検証して正規化する (感情は名前順に並べ、値 0 は除く。同じ指定が同じキャッシュキーになるように)。
    /// </summary>
    /// <returns>正規化した話者。不正なら null で、理由を <paramref name="error"/> に返す</returns>
    public static string? NormalizeSpeaker(string raw, out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var separator = raw.IndexOf(':');
        var narrator = (separator < 0 ? raw : raw[..separator]).Trim();
        if (narrator.Length == 0)
        {
            error = $"ナレーター名がありません: '{raw}'";
            return null;
        }
        if (separator < 0)
        {
            error = null;
            return narrator;
        }

        var emotions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in raw[(separator + 1)..].Split(','))
        {
            var pair = part.Split('=');
            var name = pair[0].Trim();
            if (pair.Length != 2 || name.Length == 0
                || !Int32.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                error = $"感情は 名前=値 で指定します (例: {narrator}:joy=50): '{part.Trim()}'";
                return null;
            }
            // voicepeak.exe は 101 のような範囲外の値も黙って受け付ける (実機で確認) ので、ここで検証する
            if (value is < 0 or > 100)
            {
                error = $"感情の値は 0〜100 です: {name}={value}";
                return null;
            }
            if (!emotions.TryAdd(name, value))
            {
                error = $"感情 {name} が重複しています";
                return null;
            }
        }

        error = null;
        var effective = emotions.Where(e => e.Value > 0).Select(e => $"{e.Key}={e.Value.ToString(CultureInfo.InvariantCulture)}").ToList();
        return effective.Count == 0 ? narrator : $"{narrator}:{String.Join(",", effective)}";
    }

    /// <summary>
    /// 話速・音高が VOICEPEAK で指定できる範囲か。ロックを待つ前 (<see cref="ArgumentParser"/>) に検証する。
    /// </summary>
    /// <returns>範囲外ならオプション名と理由。範囲内なら null</returns>
    public static (string Option, string Reason)? ValidateParameters(VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ToSpeed(request.Speed) is < 50 or > 200)
        {
            return ("--speed", $"VOICEPEAK では 0.5〜2.0 です: {request.Speed.ToString(CultureInfo.InvariantCulture)}");
        }
        if (ToPitch(request.Pitch) is < -300 or > 300)
        {
            return ("--pitch", $"VOICEPEAK では -0.15〜0.15 です: {request.Pitch.ToString(CultureInfo.InvariantCulture)}");
        }
        return null;
    }

    /// <summary>
    /// <c>--speed</c> (1.0 = 標準) を voicepeak.exe の <c>--speed</c> (50〜200、100 = 標準) にする。
    /// </summary>
    public static int ToSpeed(double speed) => (int)Math.Round(CacheKeyGenerator.NormalizeParameter(speed) * 100);

    /// <summary>
    /// <c>--pitch</c> (VOICEVOX と同じ目安 -0.15〜0.15) を voicepeak.exe の <c>--pitch</c> (-300〜300) にする。
    /// </summary>
    public static int ToPitch(double pitch) => (int)Math.Round(CacheKeyGenerator.NormalizeParameter(pitch) * 2000);

    /// <summary>
    /// 合成の引数。値は <c>--name=値</c> の形で渡す (<c>-</c> で始まる本文や負の音高をオプションと取り違えさせないため)。
    /// </summary>
    public static IReadOnlyList<string> BuildSynthesisArguments(VoiceRequest request, string outPath)
    {
        ArgumentNullException.ThrowIfNull(request);

        var separator = request.Speaker.IndexOf(':');
        var narrator = separator < 0 ? request.Speaker : request.Speaker[..separator];
        List<string> args =
        [
            $"--say={request.Text}",
            $"--narrator={narrator}",
            $"--out={outPath}",
            $"--speed={ToSpeed(request.Speed).ToString(CultureInfo.InvariantCulture)}",
            $"--pitch={ToPitch(request.Pitch).ToString(CultureInfo.InvariantCulture)}",
        ];
        if (separator >= 0)
        {
            args.Add($"--emotion={request.Speaker[(separator + 1)..]}");
        }
        return args;
    }

    /// <summary>
    /// 一覧の出力 (1 行 1 件) を読む。空行は除く。
    /// </summary>
    public static IReadOnlyList<string> ParseLines(string output)
        => output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary>
    /// voicepeak.exe のパス (<see cref="VoicepeakSettings.ExePath"/>、空なら既定のインストール場所)。無ければ <see cref="ErrorCodes.Engine.EngineNotAvailable"/>。
    /// </summary>
    private string ResolveExePathOrThrow()
    {
        var path = String.IsNullOrEmpty(_settings.ExePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEPEAK", "voicepeak.exe")
            : _settings.ExePath;
        if (File.Exists(path))
        {
            return path;
        }

        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.Engine.EngineNotAvailable,
            message: $"voicepeak.exe not found: {path}",
            userMessage: $"VOICEPEAK が見つかりません: {path}",
            suggestedSolution: "VOICEPEAK をインストールするか、appsettings.json の Voicepeak.ExePath に voicepeak.exe のパスを指定してください。");
    }

    /// <summary>
    /// <see cref="RunAsync"/> して、終了コードが 0 でなければ <see cref="ErrorCodes.Engine.EngineProcessError"/>。
    /// </summary>
    private async Task<ProcessResult> RunCheckedAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(exePath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Engine.EngineProcessError,
                message: $"voicepeak.exe {arguments[0]} failed (exit code {result.ExitCode}): {result.Output}",
                userMessage: $"VOICEPEAK の実行に失敗しました (終了コード {result.ExitCode}): {result.Output}");
        }
        return result;
    }

    /// <summary>
    /// voicepeak.exe を起動して終了を待つ。<see cref="VoicepeakSettings.TimeoutSeconds"/> を超えたら止めて <see cref="ErrorCodes.Api.ApiTimeout"/>。
    /// </summary>
    /// <remarks>
    /// 標準入出力はリダイレクトする (エラーの理由を拾うため。標準入力を閉じて、入力待ちで止まらないようにする)。
    /// プロセスは 1 回ごとに終わるのを待つので、Hooks のパイプを引き継いでも残らない (常駐するエンジンとは違い、
    /// <see cref="VoiceVoxEngineManager"/> の UseShellExecute の配慮は要らない)。
    /// </remarks>
    private async Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("voicepeak.exe を実行します: {Arguments}", String.Join(" ", arguments));
        var startedAt = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Engine.EngineNotAvailable,
                message: $"Failed to start voicepeak.exe: {ex.Message}",
                userMessage: $"VOICEPEAK を起動できません: {ex.Message}",
                suggestedSolution: "appsettings.json の Voicepeak.ExePath を確認してください。",
                innerException: ex);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Api.ApiTimeout,
                message: $"voicepeak.exe did not finish within {_settings.TimeoutSeconds} seconds",
                userMessage: $"VOICEPEAK が {_settings.TimeoutSeconds} 秒以内に終わりませんでした。",
                suggestedSolution: "appsettings.json の Voicepeak.TimeoutSeconds を延ばしてください。");
        }

        var output = String.Join(" ", new[] { await stdoutTask, await stderrTask }.Select(s => s.Trim()).Where(s => s.Length > 0));
        _logger.LogDebug("voicepeak.exe が終了しました - 終了コード: {ExitCode}, 所要時間: {ElapsedMs:F0}ms",
            process.ExitCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return new ProcessResult(process.ExitCode, output);
    }

    private void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // 既に終わっている
            _logger.LogDebug(ex, "voicepeak.exe の停止に失敗しました (終了済み)");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "VOICEPEAK の一時ファイルを削除できませんでした: {Path}", path);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}

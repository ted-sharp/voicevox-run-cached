using System.ComponentModel;
using System.Diagnostics;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// エンジン・ホストの起動待ちで使うプロセスの照会。
/// </summary>
public static class ProcessInstances
{
    /// <summary>
    /// 起動したプロセス (<paramref name="excludedProcessId"/>) のほかに、同じ実行ファイル (フルパス) のプロセスが動いているか。
    /// </summary>
    /// <remarks>
    /// 起動したプロセスがすぐ終わったとき、処理が起動中の別のインスタンスに渡った (終了を待たずに失敗にしてよいか) の判定に使う
    /// (<see cref="Synthesis.VoiceVoxEngineManager"/> / <see cref="Synthesis.CevioHostManager"/>)。
    /// 名前だけでは比べない。VOICEVOX・AivisSpeech のエンジンはどれも <c>run.exe</c> で、ほかのエンジンや無関係のアプリを
    /// 起動中のインスタンスとみなすと、落ちたエンジンの起動をタイムアウトまで待ってしまう。
    /// パスを読めないプロセス (権限の違うプロセス・照会中に終わったプロセス) は別の実行ファイルとみなす。
    /// </remarks>
    public static bool IsAnotherInstanceRunning(string executablePath, int excludedProcessId)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath));
        try
        {
            return processes.Any(p => p.Id != excludedProcessId
                && String.Equals(TryGetExecutablePath(p), fullPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

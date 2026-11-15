using System.Diagnostics;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// エンジン・ホストの起動待ちで使うプロセスの照会。
/// </summary>
public static class ProcessInstances
{
    /// <summary>
    /// 起動したプロセス (<paramref name="excludedProcessId"/>) のほかに、同じ実行ファイル名のプロセスが動いているか。
    /// </summary>
    /// <remarks>
    /// 起動したプロセスがすぐ終わったとき、処理が起動中の別のインスタンスに渡った (終了を待たずに失敗にしてよいか) の判定に使う
    /// (<see cref="Synthesis.VoiceVoxEngineManager"/> / <see cref="Synthesis.CevioHostManager"/>)。
    /// </remarks>
    public static bool IsAnotherInstanceRunning(string executablePath, int excludedProcessId)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        try
        {
            return processes.Any(p => p.Id != excludedProcessId);
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }
}

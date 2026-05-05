using System.Reflection;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 実行中アセンブリのバージョン取得を一箇所に集約する。
/// </summary>
public static class AssemblyVersion
{
    public static string Current { get; } =
        typeof(AssemblyVersion).Assembly.GetName().Version?.ToString() ?? "Unknown";
}

using System.Text.Json;

namespace VoicevoxRunCached.Utilities;

/// <summary>
/// JsonSerializerOptionsのキャッシュを提供するユーティリティクラス
/// </summary>
public static class JsonSerializerOptionsCache
{
    /// <summary>
    /// インデントありのJsonSerializerOptions
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// コンパクト（インデントなし）のJsonSerializerOptions
    /// </summary>
    public static JsonSerializerOptions Compact { get; } = new()
    {
        WriteIndented = false
    };
}


using System.Text.Encodings.Web;
using System.Text.Json;

namespace VoicevoxRunCached.Utilities;

/// <summary>
/// JsonSerializerOptionsのキャッシュを提供するユーティリティクラス
/// </summary>
public static class JsonSerializerOptionsCache
{
    /// <summary>
    /// インデントありのJsonSerializerOptions（日本語を直接保存）
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}


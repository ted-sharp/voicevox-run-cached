using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// 音声キャッシュキーの生成 (純粋関数)
/// </summary>
/// <remarks>
/// キーは <c>SHA256(text|speakerId|speed|pitch|volume)</c> を 16 進小文字で表現する。
/// 既存キャッシュとの互換性を壊さないため、入力フォーマットを変更してはいけない。
/// 浮動小数は InvariantCulture + F2 (小数 2 桁) で正規化する (≒ ロケール非依存)。
/// </remarks>
public static class CacheKeyGenerator
{
    public static string Compute(VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var keyString = String.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F2}|{3:F2}|{4:F2}",
            request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        // SHA256.HashData は static + スレッドセーフ。
        // 共有 SHA256 インスタンスを保持するとマルチスレッドで CryptographicException が発生する。
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

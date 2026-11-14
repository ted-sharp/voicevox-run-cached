using NAudio.Wave;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// CeVIO AI (COM) の代わり。キャスト・感情を持ち、呼び出しを記録する。
/// </summary>
internal sealed class FakeCevioRemoteService : ICevioRemoteService
{
    public const string Cast = "花隈千冬";

    /// <summary>
    /// <see cref="IsHostStarted"/> の値。既定は起動済み。
    /// </summary>
    public Func<bool> HostStarted { get; set; } = () => true;

    public Dictionary<string, List<CevioComponent>> Casts { get; } = new()
    {
        [Cast] = [new("嬉しい", 0), new("普通", 100), new("怒り", 0)],
    };

    /// <summary>
    /// <see cref="OutputWaveToFile"/> が返す値。false なら WAV を書かない。
    /// </summary>
    public bool OutputResult { get; set; } = true;

    /// <summary>
    /// <see cref="OutputWaveToFile"/> が書く WAV のサンプル値 (16bit)。
    /// </summary>
    public short SampleValue { get; set; } = 10000;

    /// <summary>
    /// 設定すると、<see cref="OutputWaveToFile"/> はこれがセットされるまで戻らない (閉じられた CeVIO AI の呼び出しが戻らないのを再現する)。
    /// </summary>
    public ManualResetEventSlim? BlockOutput { get; set; }

    public List<string> ComponentRequests { get; } = [];
    public List<CevioTalk> Talks { get; } = [];
    public int CloseHostCount { get; private set; }

    public bool IsHostStarted => HostStarted();

    public void CloseHost() => CloseHostCount++;

    public IReadOnlyList<string> GetCasts() => Casts.Keys.ToList();

    public IReadOnlyList<CevioComponent> GetComponents(string cast)
    {
        ComponentRequests.Add(cast);
        return Casts[cast];
    }

    public bool OutputWaveToFile(CevioTalk talk, string text, string path)
    {
        Talks.Add(talk);
        BlockOutput?.Wait();
        if (!OutputResult)
        {
            return false;
        }
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        for (var i = 0; i < 480; i++)
        {
            writer.WriteSample(SampleValue / 32768f);
        }
        return true;
    }
}

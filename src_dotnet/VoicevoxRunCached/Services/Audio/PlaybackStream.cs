using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 1 回の読み上げのあいだ再生デバイスに流し続ける音声。並べた音声 (本編・フィラー・間の無音) を順に渡し、
/// 並んでいない間は無音を渡す。
/// </summary>
/// <remarks>
/// <para>文ごとに再生を止めて開き直すと、その間はデバイスに何も届かない。Bluetooth のデバイスは音が来ない間は送信を休み、
/// 再開までに届いた音を捨てるので、2 文目以降の始めが切れていた (Anker SoundCore で、文の間の 0.2 秒ほどで切れた)。
/// 読み上げのあいだ無音を含めて流し続け、デバイスを休ませない。</para>
/// <para>形式は 48kHz mono float にそろえる (<see cref="Decode"/>)。合成 (24kHz)・キャッシュ (48kHz) の音声を 1 つの流れに並べるため。
/// 変換は並べる前に済ませ、デバイスが読むスレッド (<see cref="Read"/>) では変換しない。</para>
/// <para>「渡し終わった」は、デバイスが最後のサンプルを読んだ時点 (鳴り終わるのは出力のバッファの分だけ後)。</para>
/// </remarks>
public sealed class PlaybackStream : ISampleProvider
{
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    private readonly object _gate = new();
    private readonly Queue<Entry> _queue = new();
    private Entry? _current;
    private int _position;

    public WaveFormat WaveFormat => Format;

    /// <summary>
    /// 音声を <see cref="Format"/> のサンプルにする (ステレオはモノラルに混ぜ、サンプルレートを変換する)。
    /// </summary>
    public static float[] Decode(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        source = source.WaveFormat.Channels switch
        {
            1 => source,
            2 => new StereoToMonoSampleProvider(source),
            var channels => throw new NotSupportedException($"{channels} チャンネルの音声は再生できません"),
        };
        if (source.WaveFormat.SampleRate != Format.SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, Format.SampleRate);
        }

        var samples = new List<float>();
        var buffer = new float[Format.SampleRate];
        int read;
        while ((read = source.Read(buffer.AsSpan())) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }
        return [.. samples];
    }

    /// <summary>
    /// <paramref name="duration"/> の無音。
    /// </summary>
    public static float[] Silence(TimeSpan duration) => new float[(int)(duration.TotalSeconds * Format.SampleRate)];

    /// <summary>
    /// <paramref name="duration"/> のサンプル数の長さ。
    /// </summary>
    public static TimeSpan DurationOf(int sampleCount) => TimeSpan.FromSeconds((double)sampleCount / Format.SampleRate);

    /// <summary>
    /// 音声を後ろに並べる。デバイスが最後のサンプルを読んだら完了する Task を返す。
    /// </summary>
    public Task Enqueue(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // 完了の続きをデバイスのスレッド (Read の中) で走らせない
        var entry = new Entry(samples, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            _queue.Enqueue(entry);
        }
        return entry.Completion.Task;
    }

    /// <summary>
    /// 並んでいる音声をすべて捨て、待っている側に <paramref name="error"/> を伝える (デバイスのエラー・中止)。
    /// </summary>
    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        List<Entry> pending;
        lock (_gate)
        {
            pending = [.. _current is null ? [] : new[] { _current }, .. _queue];
            _current = null;
            _queue.Clear();
        }
        foreach (var entry in pending)
        {
            entry.Completion.TrySetException(error);
        }
    }

    /// <summary>
    /// デバイスが読む。並んでいる音声を渡し、足りない分は無音で埋める (常に全部を埋めて返し、デバイスを止めない)。
    /// </summary>
    public int Read(Span<float> buffer)
    {
        var written = 0;
        List<Entry>? completed = null;
        lock (_gate)
        {
            while (written < buffer.Length)
            {
                if (_current is null)
                {
                    if (!_queue.TryDequeue(out _current))
                    {
                        break;
                    }
                    _position = 0;
                }

                var n = Math.Min(buffer.Length - written, _current.Samples.Length - _position);
                _current.Samples.AsSpan(_position, n).CopyTo(buffer[written..]);
                written += n;
                _position += n;
                if (_position >= _current.Samples.Length)
                {
                    (completed ??= []).Add(_current);
                    _current = null;
                }
            }
        }

        buffer[written..].Clear();
        if (completed is not null)
        {
            foreach (var entry in completed)
            {
                entry.Completion.TrySetResult();
            }
        }
        return buffer.Length;
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    private sealed record Entry(float[] Samples, TaskCompletionSource Completion);
}

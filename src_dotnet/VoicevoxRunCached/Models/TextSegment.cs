namespace VoicevoxRunCached.Models;

public class TextSegment(string text = "")
{
    public string Text { get; set; } = text;
    public bool IsCached { get; set; }

    /// <summary>
    /// セグメントの音声。合成したものは WAV (VOICEVOX は 24kHz、VOICEPEAK・CeVIO は 48kHz)、キャッシュから読んだものは
    /// Ogg Opus (<c>SegmentProducer</c> が 48kHz の WAV にデコードして置き換えてから流す)。
    /// </summary>
    /// <remarks>
    /// 形式もサンプルレートも混在するので、読むときは必ず <c>AudioFormatDetector.CreateWaveStream</c> で形式を判定する
    /// (<c>WaveFileReader</c> で決め打ちすると Opus で失敗する)。フィラーの音声も同じく Opus (音量 1.0) か WAV (それ以外)。
    /// </remarks>
    public byte[]? AudioData { get; set; }
}

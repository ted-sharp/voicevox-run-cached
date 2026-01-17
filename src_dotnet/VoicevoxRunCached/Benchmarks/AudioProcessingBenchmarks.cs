using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Benchmarks;

[MemoryDiagnoser]
[SimpleJob]
public class AudioProcessingBenchmarks
{
    private AudioCacheManager _cacheManager = null!;
    private IMemoryCache _memoryCache = null!;
    private byte[] _sampleWavData = null!;
    private VoiceRequest _testRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        // サンプルWAVデータを生成（1秒間の44.1kHz 16bit モノラル）
        _sampleWavData = GenerateSampleWavData();

        var cacheSettings = new CacheSettings
        {
            Directory = "./benchmark-cache/",
            ExpirationDays = 30,
            MaxSizeGb = 1.0,
            UseExecutableBaseDirectory = true,
            MemoryCacheSizeMb = 100
        };
        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = cacheSettings.MemoryCacheSizeMb * 1024L * 1024L
        });
        _cacheManager = new AudioCacheManager(cacheSettings, _memoryCache);

        _testRequest = new VoiceRequest("テスト用の音声データです。", 1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cacheManager.Dispose();
        (_memoryCache as MemoryCache)?.Dispose();
    }

    [Benchmark]
    public byte[] WavToMp3Conversion()
    {
        return ConvertWavToMp3(_sampleWavData);
    }

    [Benchmark]
    public string CacheKeyGeneration()
    {
        return AudioCacheManager.ComputeCacheKey(_testRequest);
    }

    [Benchmark]
    public async Task MemoryCacheSetGet()
    {
        var key = "benchmark-test-key";
        var data = new byte[1024];
        new Random().NextBytes(data);

        _memoryCache.Set(key, data, new MemoryCacheEntryOptions().SetSize(data.Length));
        _ = _memoryCache.Get<byte[]>(key);

        await Task.CompletedTask;
    }

    [Benchmark]
    public async Task DiskCacheOperations()
    {
        var data = new byte[1024];
        new Random().NextBytes(data);

        await _cacheManager.SaveAudioCacheAsync(_testRequest, data);
        _ = await _cacheManager.GetCachedAudioAsync(_testRequest);

        await Task.CompletedTask;
    }

    private static byte[] GenerateSampleWavData()
    {
        // 1秒間の44.1kHz 16bit モノラルWAVデータを生成
        var sampleRate = 44100;
        var samples = sampleRate;
        var wavData = new byte[44 + samples * 2]; // WAVヘッダー + サンプルデータ

        // WAVヘッダー
        var header = new byte[]
        {
            0x52, 0x49, 0x46, 0x46, // RIFF
            0x00, 0x00, 0x00, 0x00, // ファイルサイズ（後で設定）
            0x57, 0x41, 0x56, 0x45, // WAVE
            0x66, 0x6D, 0x74, 0x20, // fmt
            0x10, 0x00, 0x00, 0x00, // fmtチャンクサイズ
            0x01, 0x00, // 音声フォーマット（PCM）
            0x01, 0x00, // チャンネル数（モノラル）
            0x44, 0xAC, 0x00, 0x00, // サンプルレート（44100）
            0x88, 0x58, 0x01, 0x00, // バイトレート
            0x02, 0x00, // ブロックアライメント
            0x10, 0x00, // ビット深度
            0x64, 0x61, 0x74, 0x61, // data
            0x00, 0x00, 0x00, 0x00  // データサイズ（後で設定）
        };

        Array.Copy(header, 0, wavData, 0, header.Length);

        // ファイルサイズを設定
        var fileSize = BitConverter.GetBytes(wavData.Length - 8);
        Array.Copy(fileSize, 0, wavData, 4, 4);

        // データサイズを設定
        var dataSize = BitConverter.GetBytes(samples * 2);
        Array.Copy(dataSize, 0, wavData, 40, 4);

        // サンプルデータを生成（440Hzのサイン波）
        for (int i = 0; i < samples; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 16384); // 440Hzのサイン波
            var sampleBytes = BitConverter.GetBytes(sample);
            Array.Copy(sampleBytes, 0, wavData, 44 + i * 2, 2);
        }

        return wavData;
    }

    private static byte[] ConvertWavToMp3(byte[] wavData)
    {
        try
        {
            using var wavStream = new MemoryStream(wavData);
            using var waveReader = new WaveFileReader(wavStream);
            using var outputStream = new MemoryStream();

            MediaFoundationManager.EnsureInitialized();
            MediaFoundationEncoder.EncodeToMp3(waveReader, outputStream, 128000);
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert WAV to MP3: {ex.Message}", ex);
        }
    }
}

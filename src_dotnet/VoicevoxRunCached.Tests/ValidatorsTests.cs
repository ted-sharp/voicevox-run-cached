using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Tests;

public class ValidatorsTests
{
    private static string CreateTempCacheDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "vrc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // VoiceVoxSettings ----------------------------------------------------

    [Test]
    public async Task VoiceVoxSettings_Default_IsValid()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings());
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task VoiceVoxSettings_EmptyBaseUrl_Fails()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { BaseUrl = "" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task VoiceVoxSettings_InvalidUrl_Fails()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { BaseUrl = "not-a-url" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task VoiceVoxSettings_NegativeSpeaker_Fails()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { DefaultSpeaker = -1 });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task VoiceVoxSettings_TimeoutOutOfRange_Fails()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { ConnectionTimeout = 0 });
        await Assert.That(result.IsValid).IsFalse();

        result = validator.Validate(new VoiceVoxSettings { ConnectionTimeout = 301 });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task VoiceVoxSettings_NonexistentEnginePath_Fails()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { EnginePath = "Z:/does/not/exist.exe" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task VoiceVoxSettings_EmptyEnginePath_OK()
    {
        var validator = new VoiceVoxSettingsValidator();
        var result = validator.Validate(new VoiceVoxSettings { EnginePath = "" });
        await Assert.That(result.IsValid).IsTrue();
    }

    // CacheSettings -------------------------------------------------------

    [Test]
    public async Task CacheSettings_ValidDirectory_IsValid()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new CacheSettingsValidator();
            var result = validator.Validate(new CacheSettings { Directory = dir });
            await Assert.That(result.IsValid).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CacheSettings_WritableCheckFileHeldByAnotherProcess_IsValid()
    {
        // 回帰: 書き込み検証が固定名 (.test) のファイルを使っていたため、同時に起動した別プロセスが
        // 検証中だと共有違反になり「書き込み可能である必要があります」の設定エラー (終了コード 7) になりえた
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new CacheSettingsValidator();
            using (new FileStream(Path.Combine(dir, ".test"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var result = validator.Validate(new CacheSettings { Directory = dir });
                await Assert.That(result.IsValid).IsTrue();
            }

            // 検証用のファイルは残さない
            await Assert.That(Directory.GetFiles(dir, ".write-test-*")).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CacheSettings_EmptyDirectory_Fails()
    {
        var validator = new CacheSettingsValidator();
        var result = validator.Validate(new CacheSettings { Directory = "" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task CacheSettings_ExpirationOutOfRange_Fails()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new CacheSettingsValidator();
            await Assert.That(validator.Validate(new CacheSettings { Directory = dir, ExpirationDays = 0 }).IsValid).IsFalse();
            await Assert.That(validator.Validate(new CacheSettings { Directory = dir, ExpirationDays = 3651 }).IsValid).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CacheSettings_MaxSizeOutOfRange_Fails()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new CacheSettingsValidator();
            await Assert.That(validator.Validate(new CacheSettings { Directory = dir, MaxSizeGb = 0.0 }).IsValid).IsFalse();
            await Assert.That(validator.Validate(new CacheSettings { Directory = dir, MaxSizeGb = 1001.0 }).IsValid).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments(11, false)]
    [Arguments(129, false)]
    [Arguments(12, true)]
    [Arguments(32, true)]
    [Arguments(128, true)]
    public async Task CacheSettings_OpusBitrate_Range(int bitrate, bool expected)
    {
        var dir = CreateTempCacheDir();
        try
        {
            var result = new CacheSettingsValidator().Validate(new CacheSettings { Directory = dir, OpusBitrateKbps = bitrate });
            await Assert.That(result.IsValid).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // AudioSettings -------------------------------------------------------

    [Test]
    public async Task AudioSettings_Default_IsValid()
    {
        var validator = new AudioSettingsValidator();
        var result = validator.Validate(new AudioSettings());
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task AudioSettings_OutputDeviceMinus2_Fails()
    {
        var validator = new AudioSettingsValidator();
        var result = validator.Validate(new AudioSettings { OutputDevice = -2 });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task AudioSettings_VolumeOutOfRange_Fails()
    {
        var validator = new AudioSettingsValidator();
        await Assert.That(validator.Validate(new AudioSettings { Volume = -0.01 }).IsValid).IsFalse();
        // WaveOut の音量上限は 1.0。それを超える値は効かないので設定エラーにする
        await Assert.That(validator.Validate(new AudioSettings { Volume = 1.01 }).IsValid).IsFalse();
    }

    [Test]
    public async Task AudioSettings_VolumeBoundaries_Pass()
    {
        var validator = new AudioSettingsValidator();
        await Assert.That(validator.Validate(new AudioSettings { Volume = 0.0 }).IsValid).IsTrue();
        await Assert.That(validator.Validate(new AudioSettings { Volume = 1.0 }).IsValid).IsTrue();
    }

    [Test]
    public async Task AudioSettings_DesiredLatencyOutOfRange_Fails()
    {
        var validator = new AudioSettingsValidator();
        await Assert.That(validator.Validate(new AudioSettings { DesiredLatency = 9 }).IsValid).IsFalse();
        await Assert.That(validator.Validate(new AudioSettings { DesiredLatency = 1001 }).IsValid).IsFalse();
    }

    [Test]
    public async Task AudioSettings_NumberOfBuffersOutOfRange_Fails()
    {
        var validator = new AudioSettingsValidator();
        await Assert.That(validator.Validate(new AudioSettings { NumberOfBuffers = 0 }).IsValid).IsFalse();
        await Assert.That(validator.Validate(new AudioSettings { NumberOfBuffers = 11 }).IsValid).IsFalse();
    }

    // FillerSettings ------------------------------------------------------

    [Test]
    public async Task FillerSettings_Disabled_IsValid()
    {
        var validator = new FillerSettingsValidator();
        var result = validator.Validate(new FillerSettings { Enabled = false, Directory = "" });
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task FillerSettings_EnabledRequiresDirectory()
    {
        var validator = new FillerSettingsValidator();
        var result = validator.Validate(new FillerSettings { Enabled = true, Directory = "" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task FillerSettings_EnabledRequiresNonEmptyTexts()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new FillerSettingsValidator();
            var result = validator.Validate(new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = []
            });
            await Assert.That(result.IsValid).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task FillerSettings_BlankTextEntry_Fails()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new FillerSettingsValidator();
            var result = validator.Validate(new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["valid", "  ", "another"]
            });
            await Assert.That(result.IsValid).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // LockSettings --------------------------------------------------------

    [Test]
    [Arguments(null, true)]
    [Arguments(0, true)]
    [Arguments(10, true)]
    [Arguments(-1, false)]
    public async Task LockSettings_MaxWaitSeconds(int? maxWaitSeconds, bool expectedValid)
    {
        var result = new LockSettingsValidator().Validate(new LockSettings { MaxWaitSeconds = maxWaitSeconds });
        await Assert.That(result.IsValid).IsEqualTo(expectedValid);
    }

    // LoggingSettings -----------------------------------------------------

    [Test]
    public async Task LoggingSettings_LowercaseLevels_AreValid()
    {
        var validator = new LoggingSettingsValidator();
        foreach (var level in new[] { "trace", "debug", "info", "warn", "error", "crit", "none" })
        {
            var result = validator.Validate(new LoggingSettings { Level = level, Format = "simple" });
            await Assert.That(result.IsValid).IsTrue();
        }
    }

    [Test]
    public async Task LoggingSettings_InvalidLevel_Fails()
    {
        var validator = new LoggingSettingsValidator();
        var result = validator.Validate(new LoggingSettings { Level = "verbose", Format = "simple" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task LoggingSettings_FormatRestrictedToSimpleOrJson()
    {
        var validator = new LoggingSettingsValidator();
        await Assert.That(validator.Validate(new LoggingSettings { Level = "info", Format = "simple" }).IsValid).IsTrue();
        await Assert.That(validator.Validate(new LoggingSettings { Level = "info", Format = "json" }).IsValid).IsTrue();
        await Assert.That(validator.Validate(new LoggingSettings { Level = "info", Format = "xml" }).IsValid).IsFalse();
    }

    [Test]
    public async Task LoggingSettings_FileSizeOnlyValidatedWhenFileLoggingEnabled()
    {
        var validator = new LoggingSettingsValidator();
        // ファイルロギング無効ならサイズ違反でも valid
        await Assert.That(validator.Validate(new LoggingSettings
        {
            Level = "info",
            Format = "simple",
            EnableFileLogging = false,
            MaxFileSizeMb = 0
        }).IsValid).IsTrue();

        // 有効なら不正値で fail
        await Assert.That(validator.Validate(new LoggingSettings
        {
            Level = "info",
            Format = "simple",
            EnableFileLogging = true,
            MaxFileSizeMb = 0
        }).IsValid).IsFalse();
    }

    // TestSettings --------------------------------------------------------

    [Test]
    public async Task TestSettings_EmptyMessage_Fails()
    {
        var validator = new TestSettingsValidator();
        var result = validator.Validate(new TestSettings { Message = "" });
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task TestSettings_TooLongMessage_Fails()
    {
        var validator = new TestSettingsValidator();
        var result = validator.Validate(new TestSettings { Message = new string('あ', 1001) });
        await Assert.That(result.IsValid).IsFalse();
    }

    // AppSettings (composite) --------------------------------------------

    [Test]
    public async Task AppSettings_FullValidConfiguration_PassesValidation()
    {
        var dir = CreateTempCacheDir();
        try
        {
            var validator = new AppSettingsValidator();
            var settings = new AppSettings
            {
                Cache = new CacheSettings { Directory = dir },
                Logging = new LoggingSettings { Level = "info", Format = "simple" }
            };
            var result = validator.Validate(settings);
            await Assert.That(result.IsValid).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("\\")]
    [Arguments("UPPER")]
    public async Task AppSettings_SameCacheAndFillerDirectory_FailsValidation(string variant)
    {
        // 同じディレクトリだと容量超過の削除がフィラーも消す。末尾の区切り・大文字小文字の違いでも同じとみなす。
        // --init は Filler.Enabled に関係なく生成するので、無効でも弾く
        var dir = CreateTempCacheDir();
        try
        {
            var fillerDir = variant switch
            {
                "UPPER" => dir.ToUpperInvariant(),
                _ => dir + variant,
            };
            var settings = new AppSettings
            {
                Cache = new CacheSettings { Directory = dir },
                Filler = new FillerSettings { Directory = fillerDir, Enabled = false },
                Logging = new LoggingSettings { Level = "info", Format = "simple" }
            };

            var result = new AppSettingsValidator().Validate(settings);

            await Assert.That(result.IsValid).IsFalse();
            await Assert.That(result.Errors.Any(e => e.ErrorMessage.Contains("Filler Directory"))).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AppSettings_FillerDirectoryInsideCacheDirectory_PassesValidation()
    {
        // キャッシュの走査は直下のみなので、サブディレクトリに置くのは問題ない
        var dir = CreateTempCacheDir();
        try
        {
            var settings = new AppSettings
            {
                Cache = new CacheSettings { Directory = dir },
                Filler = new FillerSettings { Directory = Path.Combine(dir, "filler") },
                Logging = new LoggingSettings { Level = "info", Format = "simple" }
            };

            var result = new AppSettingsValidator().Validate(settings);

            await Assert.That(result.IsValid).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoggingSettings_DefaultLevel_PassesValidation()
    {
        // appsettings.json で上書きされない前提のフォールバック値も validator を通る必要がある
        var validator = new LoggingSettingsValidator();
        var result = validator.Validate(new LoggingSettings());
        await Assert.That(result.IsValid).IsTrue();
    }
}

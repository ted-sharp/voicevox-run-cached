using System.Text.Json;
using Serilog.Events;
using Serilog.Parsing;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class LoggingManagerTests
{
    [Test]
    public async Task GetLogLevel_None_TurnsLoggingOff()
    {
        // 回帰: none が Fatal に対応しており、Fatal ログは出ていた
        await Assert.That(LoggingManager.GetLogLevel("none")).IsEqualTo(LevelAlias.Off);
    }

    [Test]
    [Arguments("trace", LogEventLevel.Verbose)]
    [Arguments("debug", LogEventLevel.Debug)]
    [Arguments("info", LogEventLevel.Information)]
    [Arguments("warn", LogEventLevel.Warning)]
    [Arguments("error", LogEventLevel.Error)]
    [Arguments("crit", LogEventLevel.Fatal)]
    [Arguments("DEBUG", LogEventLevel.Debug)]
    public async Task GetLogLevel_MapsNames(string name, LogEventLevel expected)
    {
        await Assert.That(LoggingManager.GetLogLevel(name)).IsEqualTo(expected);
    }

    [Test]
    public async Task JsonFormatter_WritesParsableJsonPerEvent()
    {
        // 回帰: json 指定でもテキスト書式の末尾にプロパティを JSON で付けているだけで、JSON として読めなかった
        var template = new MessageTemplateParser().Parse("キャッシュヒット: {HitCount}/{TotalCount}");
        var logEvent = new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            null,
            template,
            [
                new LogEventProperty("HitCount", new ScalarValue(1)),
                new LogEventProperty("TotalCount", new ScalarValue(2)),
            ]);

        using var output = new StringWriter();
        LoggingManager.CreateJsonFormatter().Format(logEvent, output);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);

        using var json = JsonDocument.Parse(lines[0]);
        await Assert.That(json.RootElement.GetProperty("Level").GetString()).IsEqualTo("Information");
        await Assert.That(json.RootElement.GetProperty("RenderedMessage").GetString()).IsEqualTo("キャッシュヒット: 1/2");
        await Assert.That(json.RootElement.GetProperty("Properties").GetProperty("HitCount").GetInt32()).IsEqualTo(1);
    }
}

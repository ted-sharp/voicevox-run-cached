# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a completed C# Console application that serves as a VOICEVOX REST API wrapper with intelligent audio caching functionality. The application converts text to speech using VOICEVOX engine with MP3 caching and segment-based processing for optimal performance.

## Development Commands

All development work happens in `src_dotnet/`. Navigate to the appropriate directory before running commands:

```bash
# From src_dotnet/VoicevoxRunCached/
dotnet build                          # Build project in Debug mode
dotnet run "こんにちは、世界！"        # Run with sample text
dotnet run -- test                    # Run with test message from appsettings.json
dotnet format                         # Format code (pre-commit hook)
dotnet build -c Release               # Build Release version

# From src_dotnet/
_publish.cmd                          # Build and publish standalone executable to ./publish
_publish_zip.cmd                      # Same, but creates ZIP archive
_fix-all.ps1                          # Code quality check: build, format, tests, SonarQube, ReSharper
_fix-all.ps1 -Fast                    # Quick quality check (format + build only)
```

### Running and Testing

```bash
# Basic synthesis and playback
dotnet run "このテキストを読み上げます。" --speaker 2

# Synthesis with parameters
dotnet run "速く読んでください。" --speed 1.5 --pitch 0.1

# Save to file without playing
dotnet run "ファイルに保存" --out output.mp3 --no-play

# Utilities
dotnet run devices [--full] [--json]  # List audio output devices
dotnet run speakers                    # List available speakers
dotnet run -- --init                  # Initialize filler audio cache
dotnet run -- --clear                 # Clear all audio cache files
dotnet run -c Release -- benchmark    # Run performance benchmarks (Release build only)

# Verbose mode (shows detailed timing)
dotnet run "メッセージ" --verbose
```

## Architecture Overview

### Core Request Flow

The application processes text-to-speech requests through this pipeline:

1. **CLI Entry** (Program.cs) → Parse arguments and route to CommandRouter
2. **Command Dispatch** (CommandRouter) → Route to specific command handler
3. **Segment Processing** (TextSegmentProcessor) → Split text into sentences for efficient caching
4. **Cache Lookup** (AudioCacheManager) → Check if each segment already cached (SHA256 key)
5. **API Generation** (VoiceVoxApiClient) → Generate audio for uncached segments via serial API calls
6. **Audio Playback** (AudioPlayer) → Sequential segment playback with filler audio during waits
7. **File Output** → Optionally save audio to WAV or MP3

### Key Components

- **Program.cs**: Entry point with initialization, argument parsing, and command orchestration
- **ApplicationBootstrap**: Dependency injection setup and service initialization
- **CommandRouter**: Dispatches to command handlers (speakers, devices, init, clear, synthesis)
- **VoiceVoxApiClient**: REST API communication (/audio_query, /initialize_speaker, /synthesis)
- **AudioCacheManager**: SHA256-based file caching, expiration, size limits; MP3 storage with metadata
- **TextSegmentProcessor**: Japanese sentence segmentation for optimal cache hits
- **AudioPlayer**: NAudio-based sequential playback with seamless segment transitions
- **VoiceVoxEngineManager**: Engine lifecycle (auto-start, health checks, process management)
- **FillerManager**: Generates and plays natural filler audio during segment generation waits
- **Configuration/AppSettings.cs**: Strongly-typed JSON configuration binding with FluentValidation

### Technology Stack
- **.NET 10**: Primary framework with C# 14 features
- **NAudio + NAudio.Lame**: Audio playback and MP3 encoding/decoding
- **HttpClient**: REST API communication
- **Microsoft.Extensions.Configuration**: JSON configuration management
- **Microsoft.Extensions.Logging**: Structured logging
- **Serilog**: Advanced logging with console and file outputs
- **FluentValidation**: Configuration validation
- **Polly**: Retry policies and resilience patterns
- **BenchmarkDotNet**: Performance benchmarking
- **Microsoft.Extensions.Caching.Memory**: In-memory caching

### Critical Implementation Details

**Serial API Calls**: VOICEVOX API must be called serially for each segment. The pattern is:
1. Initialize speaker (one-time per speaker ID per session)
2. For each segment: `/audio_query` (POST with text) → `/synthesis` (POST with query response)

**Cache Keys**: Generated via SHA256(speaker_id + text + parameters), stored as hex filename

**Playback Strategy**:
- Cached segments play immediately while uncached segments generate in background
- Filler audio plays during generation waits to avoid silence
- All segments play sequentially in order regardless of generation timing

**Audio Format Chain**: Text → API (WAV) → NAudio conversion → MP3 with LAME codec → Cached as .mp3 + .meta.json

### Key Design Patterns
- **Segment-based Caching**: Text split by sentences for partial cache hits on text changes
- **Background Generation**: Concurrent segment generation with streaming playback
- **MP3 Storage**: WAV-to-MP3 conversion with metadata tracking (speaker, params, hash)
- **Device Pre-warming**: Optional low-volume playback to initialize audio device
- **Polly Resilience**: Retry policies for API calls and file operations
- **Serilog Logging**: Structured logging with console and file sinks, configurable levels

## Project Structure

```
src_dotnet/VoicevoxRunCached/
├── Program.cs                      # Entry point with ApplicationBootstrap
├── Configuration/
│   ├── AppSettings.cs             # Configuration models
│   └── Validators.cs              # FluentValidation rules
├── Models/
│   ├── VoiceRequest.cs           # Request parameters
│   ├── TextSegment.cs            # Segment processing model
│   └── AudioFormat.cs            # Audio format detection
├── Services/
│   ├── ApplicationBootstrap.cs   # App initialization
│   ├── CommandRouter.cs          # Command dispatching
│   ├── ArgumentParser.cs         # CLI argument parsing
│   ├── VoiceVoxApiClient.cs      # VOICEVOX API client
│   ├── AudioCacheManager.cs      # Caching logic
│   ├── AudioPlayer.cs            # NAudio playback orchestration
│   ├── TextSegmentProcessor.cs   # Text segmentation
│   ├── VoiceVoxEngineManager.cs  # Engine lifecycle management
│   ├── FillerManager.cs          # Filler audio management
│   ├── ProgressSpinner.cs        # Console progress indication
│   ├── Audio/                    # Audio-specific services
│   ├── Cache/                    # Cache management services  
│   └── Commands/                 # Command handlers
├── Benchmarks/
│   └── AudioProcessingBenchmarks.cs # Performance benchmarks
├── Exceptions/
│   ├── VoicevoxRunCachedException.cs # Custom exceptions
│   └── ErrorCodes.cs             # Error code definitions
└── appsettings.json              # Configuration file
```

## Common Development Patterns

### Adding a New Command

1. Create handler class in `Services/Commands/` implementing ICommandHandler pattern
2. Register in ApplicationBootstrap dependency injection
3. Add routing logic in CommandRouter.RouteCommand()
4. Examples: SpeakersCommand, DevicesCommand, InitCommand

### Modifying Cache Behavior

Cache files are stored at: `{CacheDirectory}/{SHA256_HEX}.mp3` with `{SHA256_HEX}.meta.json`
- Do NOT change SHA256 generation logic (breaks all existing cache)
- Metadata JSON stores: speaker_id, parameters, original text hash
- Cache validation: check metadata matches before playing

### Working with Text Segmentation

TextSegmentProcessor splits on Japanese sentence boundaries. Key methods:
- `SplitIntoSegments(text)`: Returns list of TextSegment objects
- TextSegment has: Text, Parameters, CacheKey properties
- Empty segments are filtered; whitespace-only text returns empty list
- Cache hit/miss determined per-segment, not whole text

### Handling API Calls

VoiceVoxApiClient is serial by design:
- All calls locked via `_synthesisLock` (Lock type for thread-safety)
- If modifying to add parallelism, update comments and verify API constraints
- Check for HttpRequestException and connection timeouts
- Speaker initialization happens automatically on first encounter

### Logging Conventions

Use Serilog through ILogger:
- Error level: recoverable failures requiring user action
- Warning level: unexpected conditions but continued operation
- Information: major milestones (engine started, cache cleared)
- Debug: detailed flow (segment processing, cache hits)
- Avoid DEBUG prefix in log messages (user prefers clean output)

## Configuration

The application uses `appsettings.json` with these sections:
- **VoiceVox**: EngineType, BaseUrl, EngineArguments, DefaultSpeaker, ConnectionTimeout, EnginePath, AutoStartEngine, StartupTimeoutSeconds, KeepEngineRunning
- **Cache**: Directory, UseExecutableBaseDirectory, ExpirationDays, MaxSizeGB
- **Audio**: OutputDevice (-1 for default), Volume, PrepareDevice, PreparationDurationMs, PreparationVolume, OutputDeviceId
- **Filler**: Enabled, Directory, UseExecutableBaseDirectory, FillerTexts
- **Logging**: Level, Format (legacy - mostly replaced by Serilog)
- **Serilog**: MinimumLevel, WriteTo (Console/File), OutputTemplate, Enrichers
- **Test**: Message (comprehensive test text for development)

## Pre-Commit Hooks and Code Quality

The project uses pre-commit hooks (`.pre-commit-config.yaml`) that automatically run on `git commit`:

1. **dotnet-build**: Verifies Release build succeeds
2. **dotnet-format**: Auto-formats C# code (idempotent, safe to run anytime)
3. **dotnet-test**: Runs test projects if they exist (currently skipped as no tests)
4. **File checks**: Trailing whitespace, EOF fixes, YAML/JSON validation, merge conflict detection

**To manually run quality checks**:
```bash
# From src_dotnet/ directory
_fix-all.ps1 -Fast              # Quick: format + build only
_fix-all.ps1                    # Full: format + build + tests + SonarQube + ReSharper
```

**Pre-commit hooks environment**: Uses bash and dotnet CLI, works on Windows with MSYS2

## Command Interface

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init
VoicevoxRunCached --clear
VoicevoxRunCached benchmark
VoicevoxRunCached test

Options:
--speaker, -s <id>    Speaker ID (default: 1)
--speed <value>       Speech speed (default: 1.0)
--pitch <value>       Speech pitch (default: 0.0)
--volume <value>      Speech volume (default: 1.0)
--no-cache           Skip cache usage
--cache-only         Use cache only, don't call API
--out, -o <path>     Save output audio to file (.wav or .mp3)
--no-play            Do not play audio (useful with --out)
--verbose            Show detailed timing information
--log-level <level>  Log level: trace|debug|info|warn|error|crit|none
--log-format <fmt>   Log format: simple|json
--help, -h           Show help message

Commands:
speakers             List available speakers
devices              List audio output devices
--init               Initialize filler audio cache
--clear              Clear all audio cache files
benchmark            Run performance benchmarks (requires Release build)
test                 Run test with comprehensive message from appsettings.json
```

## Development Notes

### Architecture Patterns
- **Command Pattern**: Commands handled through CommandRouter and specific command handlers
- **Service Locator**: ApplicationBootstrap centralizes service initialization
- **Repository Pattern**: AudioCacheManager abstracts caching operations
- **Chain of Responsibility**: Audio processing pipeline with multiple stages

### Technical Constraints

**API Requirements**:
- All VOICEVOX API calls must be serial (thread-safe queue enforcement)
- Speaker must be initialized once per speaker ID per session via `/initialize_speaker`
- Connection timeout: configurable (default 30s), applies to all HTTP requests
- Invalid speaker IDs should be caught before first API call

**Caching Rules**:
- Cache keys: SHA256(speaker_id + raw_text + speed + pitch + volume)
- Audio stored as .mp3 files with accompanying .meta.json (contains parameters and hash)
- Expiration configurable via ExpirationDays setting
- Size-based eviction when MaxSizeGB limit exceeded
- Cache is relative to executable location (UseExecutableBaseDirectory: true)

**Text Processing**:
- Text is split by sentence boundaries (。! ? for Japanese)
- Empty segments filtered out
- Each segment cached independently for partial reuse on text changes

**Engine Management**:
- Auto-start checks for existing VOICEVOX processes before launching
- Supports both VOICEVOX and AIVIS Speech engines via configuration
- Engine can be kept running (KeepEngineRunning: true) or stopped on app exit
- Engine path auto-detected from common installation locations if EnginePath empty

**Audio Device Handling**:
- OutputDevice: -1 = system default, 0+ = specific device index
- PrepareDevice option plays silent audio to initialize device and prevent dropouts
- Optional OutputDeviceId allows targeting specific WASAPI endpoint

### Code Standards
- **C# Version**: Target .NET 10 with C# 14 features (primary constructors, collection expressions, etc.)
- **Null Safety**: Nullable reference types enabled; all public APIs should validate inputs
- **Configuration**: FluentValidation validators for AppSettings sections (Validators.cs)
- **Logging**: Serilog structured logging; avoid simple Console.WriteLine
- **Resilience**: Polly retry policies for HTTP calls and file operations
- **Testing**: No test projects currently; pre-commit hooks verify build and format
- **Exception Handling**: Custom VoicevoxRunCachedException for domain errors; let framework exceptions surface
- **Performance**: BenchmarkDotNet for profile-driven optimization; results in Benchmarks/ subdirectory

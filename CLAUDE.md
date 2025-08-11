# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a completed C# Console application that serves as a VOICEVOX REST API wrapper with intelligent audio caching functionality. The application converts text to speech using VOICEVOX engine with MP3 caching and segment-based processing for optimal performance.

## Development Commands

Navigate to `src_dotnet/VoicevoxRunCached/` for all development operations:

```bash
# Build the project
dotnet build

# Run the application with test text
dotnet run "こんにちは、世界！"

# Run with specific speaker
dotnet run "テストメッセージです。" --speaker 1

# List audio devices
dotnet run devices --full

# Build release version
dotnet build -c Release

# Publish standalone executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish

# Use the publish script (Windows)
src_dotnet/_publish.cmd
```

## Architecture Overview

### Core Components
- **Program.cs**: Entry point with command-line parsing and orchestration
- **VoiceVoxApiClient**: REST API communication with VOICEVOX server
- **AudioCacheManager**: Intelligent file-based caching with SHA256 hashing and MP3 compression
- **AudioPlayer**: NAudio-based audio playbook with sequential segment playback
- **TextSegmentProcessor**: Text segmentation for cache optimization
- **VoiceVoxEngineManager**: VOICEVOX engine lifecycle management and auto-start functionality
- **FillerManager**: Intelligent filler audio management for natural speech gaps
- **ProgressSpinner**: Console progress indication during operations
- **Configuration/AppSettings.cs**: Strongly-typed configuration management

### Technology Stack
- **.NET 9.0**: Primary framework
- **NAudio + NAudio.Lame**: Audio playback and MP3 encoding/decoding
- **HttpClient**: REST API communication
- **Microsoft.Extensions.Configuration**: JSON configuration management
- **System.Text.Json**: JSON serialization

### Data Flow Architecture
1. **Text Segmentation**: Input text split into sentences for optimal caching
2. **Cache Lookup**: SHA256-based cache key generation and lookup for each segment
3. **Parallel Processing**: Cached segments play immediately while uncached generate in background
4. **VOICEVOX API**: Serial API calls (/audio_query → /synthesis) with speaker initialization
5. **MP3 Caching**: WAV-to-MP3 conversion for efficient storage
6. **Sequential Playback**: Segments play in order with seamless transitions

### Key Design Patterns
- **Segment-based Caching**: Text split by sentences for partial cache hits
- **Background Generation**: Uncached segments generate while cached segments play
- **MP3 Compression**: Audio stored as MP3 files with .meta.json metadata
- **Device Pre-warming**: Audio device initialization to prevent audio dropouts

## Project Structure

```
src_dotnet/VoicevoxRunCached/
├── Program.cs                      # Entry point and CLI handling
├── Configuration/
│   └── AppSettings.cs             # Configuration models
├── Models/
│   ├── VoiceRequest.cs           # Request parameters
│   └── TextSegment.cs            # Segment processing model
├── Services/
│   ├── VoiceVoxApiClient.cs      # VOICEVOX API client
│   ├── AudioCacheManager.cs      # Caching logic
│   ├── AudioPlayer.cs            # NAudio playback
│   ├── TextSegmentProcessor.cs   # Text segmentation
│   ├── VoiceVoxEngineManager.cs  # Engine lifecycle management
│   ├── FillerManager.cs          # Filler audio management
│   └── ProgressSpinner.cs        # Console progress indication
└── appsettings.json              # Configuration file
```

## Configuration

The application uses `appsettings.json` with these sections:
- **VoiceVox**: EngineType, BaseUrl, EngineArguments, DefaultSpeaker, ConnectionTimeout, EnginePath, AutoStartEngine, StartupTimeoutSeconds, KeepEngineRunning
- **Cache**: Directory, UseExecutableBaseDirectory, ExpirationDays, MaxSizeGB
- **Audio**: OutputDevice (-1 for default), Volume, PrepareDevice, PreparationDurationMs, PreparationVolume, OutputDeviceId
- **Filler**: Enabled, Directory, UseExecutableBaseDirectory, FillerTexts
- **Logging**: Level, Format

## Command Interface

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init
VoicevoxRunCached --clear

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
```

## Development Notes

- All VOICEVOX API calls must be serial (no parallel requests)
- Speaker initialization required before first use of each speaker
- Cache files use SHA256 hashing for unique identification
- Audio files stored as MP3 with accompanying .meta.json metadata
- Text segmentation optimizes cache efficiency for partial text changes
- Engine auto-start functionality checks for existing processes before launching
- Filler audio uses separate cache directory for interval management
- ProgressSpinner provides non-blocking console feedback during operations
- All services use primary constructors and modern C# 13 patterns

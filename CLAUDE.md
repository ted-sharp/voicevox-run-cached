# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a C# Console application that serves as a VOICEVOX REST API wrapper with audio caching functionality. The project is currently in the requirements phase with detailed specifications documented but no actual code implementation yet.

## Key Architecture Concepts

Based on the requirements document (`doc/voicevox_wrapper_requirements.md`), the planned architecture includes:

### Core Components
- **VoiceVoxApiClient**: Handles REST API communication with VOICEVOX server
- **AudioCacheManager**: File-based caching system using SHA256 hashing
- **AudioPlayer**: NAudio-based audio playback functionality
- **Configuration**: appsettings.json-based configuration management

### Technology Stack
- **.NET Core**: Primary framework
- **NAudio**: Audio playback (MP3/WAV support)
- **HttpClient**: REST API communication
- **System.CommandLine**: Command-line argument parsing
- **Microsoft.Extensions.Configuration**: Configuration management

### Data Flow
1. Command-line input parsing
2. Cache lookup by SHA256 hash
3. VOICEVOX API calls (/audio_query → /synthesis)
4. Audio caching and playback via NAudio

## Development Commands

Since no code exists yet, standard .NET commands will apply once implementation begins:
- `dotnet build` - Build the project
- `dotnet run` - Run the application
- `dotnet test` - Run tests (when implemented)

## Key Design Decisions

### Cache Strategy
- File-based cache in `./cache/audio/` directory
- SHA256 hash-based file naming
- Metadata stored in `.meta.json` files
- Configurable expiration and size limits

### API Constraints
- VOICEVOX requires serial processing (no parallel requests)
- Speaker initialization needed for first use
- Default speaker: Zundamon (ID: 1)

### Command Interface
```
VoicevoxRunCached.exe <text> [--speaker <id>] [--speed <value>] [--pitch <value>] [--volume <value>] [--no-cache] [--cache-only] [--speakers] [--help]
```

## Project Status

The application has been fully implemented and includes all core functionality:
1. **Stage 1**: MVP with basic text-to-speech
2. **Stage 2**: Full speaker support and caching
3. **Stage 3**: Configuration and error handling
4. **Stage 4**: Advanced features and optimizations

## Configuration Structure

The application will use `appsettings.json` with sections for:
- `VoiceVox`: API endpoint and connection settings
- `Cache`: Directory, expiration, and size limits  
- `Audio`: Output device and volume settings
// 物理フォルダごとに subnamespace を切るが、cross-folder 参照が頻繁なので
// 主要なサブネームスペースは global using で一括公開する。
global using VoicevoxRunCached.Services.Audio;
global using VoicevoxRunCached.Services.Cache;
global using VoicevoxRunCached.Services.Commands;
global using VoicevoxRunCached.Services.Infrastructure;
global using VoicevoxRunCached.Services.Synthesis;

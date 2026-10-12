using System.Text.Json;

namespace Muyu.Core;
public sealed record AudioCommand(string Action, Track? Track = null, string? Device = null, bool Shared = false, long Frame = 0, QueueItem[]? Segments = null, Guid? ItemId = null,AudioSettings? Settings=null,IReadOnlyDictionary<string,SourceFingerprint>? Evidence=null);
public sealed record AudioReply(AudioStatus Status, string Error, bool Failed = false, Guid? ItemId = null, bool ContinuousCue = false,AudioActual? Actual=null,string[]? AsioDrivers=null,DeviceCapability? Capability=null);
public static class AudioProtocol { public static JsonSerializerOptions Options { get; } = new() { IncludeFields = true }; }

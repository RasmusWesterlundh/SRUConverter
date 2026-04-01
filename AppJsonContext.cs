using System.Text.Json.Serialization;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter;

/// <summary>
/// Source-generated JSON serialization context for trim-safe publishing.
/// Covers all types serialized/deserialized at runtime.
/// </summary>
[JsonSerializable(typeof(PersonInfo))]
[JsonSerializable(typeof(List<Observation>))]
[JsonSerializable(typeof(List<ObservationAggregate>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext { }

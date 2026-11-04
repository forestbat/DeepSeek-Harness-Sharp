using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Ide.Triggers;

namespace Dsh.Ide.Surfaces;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EditorRequest))]
[JsonSerializable(typeof(EditorResult))]
[JsonSerializable(typeof(EditorDecision))]
[JsonSerializable(typeof(EditorWorkspace))]
[JsonSerializable(typeof(EditorBinding))]
[JsonSerializable(typeof(IReadOnlyList<EditorInstallation>))]
[JsonSerializable(typeof(IReadOnlyList<EditorDiagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<EditorTrigger>))]
[JsonSerializable(typeof(IReadOnlyList<EditorTriggerOutcome>))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;

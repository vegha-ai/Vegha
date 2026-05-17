using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits a copy-pasteable code snippet for a given request in a target language.</summary>
public interface ICodegenEmitter
{
    /// <summary>Short language identifier used in UI (e.g. "curl", "javascript", "python").</summary>
    string Language { get; }

    /// <summary>Display label (e.g. "cURL", "JavaScript fetch").</summary>
    string DisplayName { get; }

    /// <summary>Produce the code snippet. Optional vars are interpolated into the URL/headers/body before emission.</summary>
    string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null);
}

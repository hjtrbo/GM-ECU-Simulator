using System.Text.Json;

namespace Core.Security;

// A pluggable handler for GMW3110 $27 SecurityAccess. The simulator delegates
// one full $27 exchange step (one requestSeed OR one sendKey) per call to
// Handle. The module receives a SecurityAccessContext that exposes the
// decoded USDT payload, mutable NodeState, the current bus time, and an
// ISecurityEgress for putting frames back on the bus — modules never need
// to import the ISO-TP transport layer directly.
//
// Most consumers do NOT implement this directly: instead they write a small
// ISeedKeyAlgorithm and let the bundled Gmw3110_2010_Generic module handle
// the protocol envelope. ISecurityAccessModule is the escape hatch for
// non-standard protocol flows (e.g. multi-message exchanges, MAC-based
// schemes) that the generic module's request/response shape can't express.
public interface ISecurityAccessModule
{
    /// <summary>Stable identifier persisted in ecu_config.json's SecurityModuleId.</summary>
    string Id { get; }

    /// <summary>
    /// Process one $27 USDT request. The implementation must enqueue exactly
    /// one response (positive, negative, or raw) via ctx.Egress before
    /// returning, unless it intentionally stays silent (rare — typically only
    /// for functional broadcasts, which the dispatcher already filters).
    /// </summary>
    void Handle(SecurityAccessContext ctx);

    /// <summary>
    /// Apply module-specific configuration deserialised from the EcuDto's
    /// SecurityModuleConfig JsonElement. Called once at config load and
    /// again whenever the user edits settings in the editor. May be null
    /// when no config was supplied.
    /// </summary>
    void LoadConfig(JsonElement? config);
}

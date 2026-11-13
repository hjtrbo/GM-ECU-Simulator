using System.Text.Json;

namespace Core.Security.Modules;

// A model limitation, not a claim about the real controller's negative reply.
// Imported selectors are evidence, not permission to substitute an ECU cipher
// or an accept-any-key fixture. An explicit user-selected model remains possible.
public sealed class UnresolvedArchiveSecurityModule : ISecurityAccessModule
{
    public string Id => Core.Dps.UtilitySecuritySelection.UnresolvedModuleId;
    public SecurityModuleBehaviour Behaviour => SecurityModuleBehaviour.Strict;
    public void LoadConfig(JsonElement? config) { }
    public void Handle(SecurityAccessContext ctx)
        => ctx.Egress.SendNegativeResponse(0x11); // service not supported by this model
}

using Core.Bus;
using Core.Ecu.Personas;
using Core.Security;
using System.Text.Json;

namespace Core.Ecu;

// Shared foundation for every ECU we create from external data (bin file
// or DPS archive). Both ArchivePrimer.BuildEcuNode and the new
// BinEcuFactory used to need the same opening 30 lines: pick the next
// OBD-II CAN-ID triple, mark IsPrimed=true so EcuIdentitySeeder skips
// the node, install a security module. That boilerplate now lives here
// and the two factories just call CreatePrimed before continuing with
// their own data-population logic.
public static class EcuNodeFactory
{
    /// <summary>
    /// One OBD-II ECU's CAN identity. The request / response triple is the
    /// 11-bit OBD-II convention real GM vehicles use (request $7E0+, USDT
    /// response = req + 8, UUDT response = req - $1F8); see the comment on
    /// <see cref="EcuNode.PhysicalRequestCanId"/> for the rationale.
    /// </summary>
    public sealed record CanIds(
        ushort PhysicalRequestId,
        ushort UsdtResponseId,
        ushort UudtResponseId,
        byte DiagnosticAddress);

    // OBD-II CAN-ID triple defaults for the first ECM. Same constants
    // ArchivePrimer used inline; kept here as the canonical home now that
    // two factories reference them.
    public const ushort DefaultEcmRequestId  = 0x7E0;
    public const ushort DefaultEcmResponseId = 0x7E8;
    public const ushort DefaultEcmUudtId     = 0x5E8;
    public const byte   DefaultEcmDiagAddress = 0x11;

    /// <summary>
    /// Pick the next free OBD-II CAN-ID triple on <paramref name="bus"/>, starting
    /// at <c>$7E0/$7E8/$5E8</c> and incrementing the request id by 2 until a
    /// vacancy is found. The diagnostic address tracks the request id with
    /// canonical GM values for the well-known slots: ECM=$11, TCM=$18,
    /// FSCM=$28, ICCM=$29, then $11 + (req - $7E0) as a deterministic
    /// fallback. This is the same convention <c>MainViewModel.AddEcu</c>
    /// used before the refactor, with the diagnostic-address slot filled
    /// in (the old code left it at 0, which made $1A $B0 return a zero
    /// byte rather than the canonical ECM/TCM diag address).
    /// </summary>
    public static CanIds NextObd2EcmTripleFor(VirtualBus bus)
    {
        ushort req = DefaultEcmRequestId;
        while (bus.FindByRequestId(req) != null && req < 0x7E8) req += 2;
        byte diag = CanonicalDiagAddressFor(req);
        return new CanIds(
            PhysicalRequestId: req,
            UsdtResponseId:    (ushort)(req + 0x008),
            UudtResponseId:    (ushort)(req - 0x1F8),  // 0x7E0 -> 0x5E8
            DiagnosticAddress: diag);
    }

    private static byte CanonicalDiagAddressFor(ushort requestId) => requestId switch
    {
        0x7E0 => 0x11,   // ECM
        0x7E2 => 0x18,   // TCM
        0x7E4 => 0x28,   // FSCM / fuel
        0x7E6 => 0x29,   // ICCM / instrument cluster
        _     => (byte)(0x11 + (requestId - 0x7E0)),
    };

    /// <summary>
    /// Build a bare, primed <see cref="EcuNode"/>: identity fields populated
    /// from <paramref name="ids"/>, security module installed, IsPrimed=true
    /// so <c>EcuIdentitySeeder</c> skips it. No PIDs, no identifiers - the
    /// calling factory populates those from its own data source.
    /// </summary>
    /// <param name="name">Display name shown in the ECU list.</param>
    /// <param name="ids">CAN-ID + diagnostic-address tuple, typically from
    /// <see cref="NextObd2EcmTripleFor"/>.</param>
    /// <param name="securityModuleId">Registry id for the $27 module, e.g.
    /// <c>"gm-bypass-2byte"</c> or <c>"gm-algo92-5byte"</c>. Pass an unknown
    /// id to install no module (the node will NRC $27 ServiceNotSupported).</param>
    /// <param name="securityConfig">Module-specific JSON config, applied via
    /// <see cref="ISecurityAccessModule.LoadConfig"/>. Null for modules with
    /// no parameters (e.g. the bypass).</param>
    public static EcuNode CreatePrimed(
        string name,
        CanIds ids,
        string securityModuleId,
        JsonElement? securityConfig)
    {
        var node = new EcuNode
        {
            IsPrimed             = true,
            Name                 = name,
            PhysicalRequestCanId = ids.PhysicalRequestId,
            UsdtResponseCanId    = ids.UsdtResponseId,
            UudtResponseCanId    = ids.UudtResponseId,
            DiagnosticAddress    = ids.DiagnosticAddress,
            ProgrammedState      = 0x00,   // FullyProgrammed, GMW3110 §8.16
            Persona              = Gmw3110Persona.Instance,
        };

        node.SecurityModule = SecurityModuleRegistry.Create(securityModuleId);
        node.SecurityModuleConfig = securityConfig;
        node.SecurityModule?.LoadConfig(securityConfig);
        return node;
    }
}

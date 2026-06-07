using Core.Security.Algorithms;
using Core.Security.Modules;

namespace Core.Security;

// String-ID -> factory map for ISecurityAccessModule instances. Per-ECU
// instances (each EcuNode owns its own), so each module gets independent
// bookkeeping. Built-ins register in the static ctor; a future DLL loader
// can call Register the same way without touching anything else.
//
// Naming axis (post-2026-05-18 cleanup): "gm-{ecmFamily}-{width}" for strict
// entries, "gm-bypass-{width}" for permissive entries. The "algo92" /
// "algo89" attribution from community sources is too unsourced to be a load-
// bearing identifier - DPS only confirms "Algo 92" for the 5-byte E92 path.
// Naming by ECM family is honest about what each cipher actually targets and
// keeps the width suffix to disambiguate the two E-family ECMs that share a
// cipher footprint. Legacy IDs from every prior naming attempt are remapped
// at config-load time via NormaliseLegacyId.
public static class SecurityModuleRegistry
{
    private static readonly Dictionary<string, Func<ISecurityAccessModule>> Factories = new();

    // Old-id -> current-id alias table. Applied by ConfigStore on load so
    // existing ecu_config.json files keep working after the cleanup. Keep
    // this in sync with anything that hardcodes IDs (ArchivePrimer etc.).
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gm-e38"]                       = "gm-e38-2byte",
        ["gm-e67"]                       = "gm-e67-2byte",
        ["gm-t43"]                       = "gm-t43-2byte",
        ["gm-e92"]                       = "gm-e92-5byte",
        ["gm-algo-92"]                   = "gm-e92-5byte",
        // Short-lived algo-axis names from the first half of the 2026-05-18
        // cleanup. Aliased for any config saved in that window.
        ["gm-algo92-2byte"]              = "gm-e38-2byte",
        ["gm-algo89-2byte"]              = "gm-e67-2byte",
        ["gm-algo92-5byte"]              = "gm-e92-5byte",
        ["gm-programming-bypass"]        = "gm-bypass-2byte",
        ["gm-permissive-5byte"]          = "gm-bypass-5byte",
        ["gmw3110-2010-not-implemented"] = "gm-bypass-2byte",
    };

    static SecurityModuleRegistry()
    {
        // Strict ciphers. Each wraps a real seed/key algorithm class and runs
        // the full GMW3110 SecurityAccess flow; mismatched keys get NRC $35.

        // E38 ECM, 2-byte cipher used by non-DPS testers (HP Tuners,
        // EFILive, jakka351 tooling) in an ExtendedDiagnosticSession ($10 03).
        // Math: k = ~(bswap(s)+0x7D58)+0x8001. Community-attributed to "GMLAN
        // 0x92" but the algorithm-number attribution is unsourced - DPS only
        // confirms "Algo 92" for the 5-byte E92 path (see gm-e92-5byte).
        Register("gm-e38-2byte",
            () => new Gmw3110_2010_Generic(new E38Algorithm(),
                                           id: "gm-e38-2byte"));

        // E92 ECM, 5-byte cipher: DPS "Algo 92". Reverse-engineered on
        // 2026-05-17 via a logging proxy (tools/sa015bcr_hook/); password
        // table in Gm5BytePasswords. The "92" here is grounded: it's the
        // algoId byte DPS utility files for this family carry.
        Register("gm-e92-5byte",
            () => new Gmw3110_2010_Generic(new Gm5ByteAlgorithm(),
                                           id: "gm-e92-5byte"));

        // E67 ECM, 2-byte cipher. Extracted from PowerPCM_Flasher_0006's
        // KeyAlgoGm_$89 (RVA 0x6670). Brute-force-distinct from gm-e38-2byte
        // over all 65536 seeds despite both being community-tagged "GMLAN".
        Register("gm-e67-2byte",
            () => new Gmw3110_2010_Generic(new E67Algorithm(),
                                           id: "gm-e67-2byte"));

        // gett43key for the 6T70 TCM (T43). GM algorithm number not yet
        // documented; rename to gm-algoNN-2byte if a utility-file `27 NN` byte
        // sequence or vendor doc confirms it.
        Register("gm-t43-2byte",
            () => new Gmw3110_2010_Generic(new T43Algorithm(),
                                           id: "gm-t43-2byte"));

        // Bypass entries. Emit a non-zero random seed (or fixedSeed config)
        // and accept any key. Used for tester-side convenience and for
        // modelling stub-security ECUs; both 2- and 5-byte widths so DPS
        // gets the seed length its utility file expects.
        Register("gm-bypass-2byte",
            () => new Gmw3110_2010_Generic(new RandomSeedCipher(2),
                                           id: "gm-bypass-2byte",
                                           behaviour: SecurityModuleBehaviour.BypassAll));

        Register("gm-bypass-5byte",
            () => new Gmw3110_2010_Generic(new RandomSeedCipher(5),
                                           id: "gm-bypass-5byte",
                                           behaviour: SecurityModuleBehaviour.BypassAll));

        // Ford UDS accept-any-key module for the ford-uds flash path. Issues
        // a real (non-zero) seed and accepts whatever key the tester computes -
        // we don't have PCMTec's seed/key algorithm for this PCM, so this is the
        // honest way to walk a Ford flash tool past $27 and into the write
        // services. Seed width / fixed seed are set via SecurityModuleConfig.
        // See FordUdsAcceptAnyKeyModule for the full rationale.
        Register("ford-uds-accept-any",
            () => new FordUdsAcceptAnyKeyModule(id: "ford-uds-accept-any"));
    }

    public static void Register(string id, Func<ISecurityAccessModule> factory)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Module id must be non-empty", nameof(id));
        Factories[id] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Creates a new instance of the module with the given id, or null if id is null/unknown.</summary>
    public static ISecurityAccessModule? Create(string? id)
    {
        if (id is null) return null;
        var resolved = NormaliseLegacyId(id);
        return Factories.TryGetValue(resolved, out var factory) ? factory() : null;
    }

    /// <summary>All registered module IDs. Used to populate the editor's module picker.</summary>
    public static IReadOnlyCollection<string> KnownIds => Factories.Keys;

    /// <summary>
    /// Maps deprecated IDs to their current equivalents. Returns the input
    /// unchanged when no alias exists, including for already-current IDs.
    /// Called from ConfigStore on load and from Create so any code path
    /// that still references a legacy ID resolves to a working module.
    /// </summary>
    public static string NormaliseLegacyId(string id)
        => LegacyAliases.TryGetValue(id, out var current) ? current : id;
}

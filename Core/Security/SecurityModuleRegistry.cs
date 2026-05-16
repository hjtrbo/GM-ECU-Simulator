using Core.Security.Algorithms;
using Core.Security.Modules;

namespace Core.Security;

// String-ID → factory map for ISecurityAccessModule instances. Per-ECU
// instances (each EcuNode owns its own), so each module gets independent
// bookkeeping. Built-ins register in the static ctor; a future DLL loader
// can call Register the same way without touching anything else.
public static class SecurityModuleRegistry
{
    private static readonly Dictionary<string, Func<ISecurityAccessModule>> Factories = new();

    static SecurityModuleRegistry()
    {
        Register("gmw3110-2010-not-implemented",
            () => new Gmw3110_2010_Generic(new NotImplementedAlgorithm(),
                                           id: "gmw3110-2010-not-implemented"));
        Register("gm-algo-92",
            () => new Gmw3110_2010_Generic(new E38Algorithm(),
                                           id: "gm-algo-92"));
        // GM algorithm number not yet documented for T43 - rename to gm-algo-NN
        // once a TCM utility-file `27 NN` byte sequence or vendor doc confirms it.
        // (Searched Interpreters_September_01_2009.docx and the 2011 DPS Programmers
        // Reference Manual: both define the op-code language, not per-controller
        // algorithm assignments. OpCodeAssessment.xls shows a TCM with algorithm
        // 0x84 but its part numbers don't match the T43 6T70 family, so 0x84
        // can't be confidently attributed to T43.)
        Register("gm-t43",
            () => new Gmw3110_2010_Generic(new T43Algorithm(),
                                           id: "gm-t43"));
        Register("gm-programming-bypass",
            () => new Gmw3110_2010_Generic(new Gmw3110ProgrammingBypassAlgorithm(),
                                           id: "gm-programming-bypass"));
    }

    public static void Register(string id, Func<ISecurityAccessModule> factory)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Module id must be non-empty", nameof(id));
        Factories[id] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Creates a new instance of the module with the given id, or null if id is null/unknown.</summary>
    public static ISecurityAccessModule? Create(string? id)
        => id is not null && Factories.TryGetValue(id, out var factory) ? factory() : null;

    /// <summary>All registered module IDs. Used to populate the editor's module picker.</summary>
    public static IReadOnlyCollection<string> KnownIds => Factories.Keys;
}

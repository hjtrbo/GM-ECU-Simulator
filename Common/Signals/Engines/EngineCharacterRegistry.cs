namespace Common.Signals.Engines;

// The named set of engine characters the simulator can run, keyed by a stable string id stored in config and shown in
// the editor dropdown. Mirrors Core.Security.SecurityModuleRegistry: adding a new engine is a single Register(...)
// call plus a class - nothing else changes. The default (na-gas-v8) reproduces the simulator's original naturally-
// aspirated behaviour, so a config with no engine id - i.e. every config saved before this existed - loads unchanged.
public static class EngineCharacterRegistry
{
    public const string DefaultId = "na-gas-v8";

    private static readonly Dictionary<string, Func<IEngineCharacter>> Factories =
        new(StringComparer.OrdinalIgnoreCase);

    static EngineCharacterRegistry()
    {
        Register(DefaultId, () => new NaGasV8());
        Register("boosted-gas-v8", () => new BoostedGasV8());
    }

    // Register (or replace) a character factory under an id. A future engine plugs in here with one line.
    public static void Register(string id, Func<IEngineCharacter> factory) => Factories[id] = factory;

    // Create the character for an id, falling back to the default for a null or unknown id so a hand-edited or future
    // config can never leave an ECU without an engine.
    public static IEngineCharacter Create(string? id)
        => id != null && Factories.TryGetValue(id, out var f) ? f() : Factories[DefaultId]();

    public static bool IsKnown(string? id) => id != null && Factories.ContainsKey(id);

    public static IReadOnlyCollection<string> KnownIds => Factories.Keys;

    // (id, displayName) for every registered character, for binding the editor dropdown.
    public static IReadOnlyList<(string Id, string DisplayName)> Catalogue =>
        Factories.Keys.Select(id => (id, Factories[id]().DisplayName)).ToList();
}

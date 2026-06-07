using Xunit;

namespace EcuSimulator.Tests.TestHelpers;

// FordUdsPersona is a singleton carrying process-wide mutable static state:
// the captured $A1 DMR slot map, the $A0 broadcast timer, the per-session log
// writer, and the $23 flash-bin backing. xUnit parallelises test CLASSES by
// default, so any two classes that drive that singleton would otherwise mutate
// those statics concurrently - which surfaced as a flaky DMR-slot leak where a
// broadcast frame carried a slot bound by a different class's test.
//
// Every test class that touches the FordUdsPersona singleton's statics
// declares [Collection(Name)]; xUnit runs same-collection classes sequentially
// while the rest of the suite still parallelises. Module-only tests that
// construct their own objects (e.g. FordUdsAcceptAnyKeyModuleTests) don't need
// to join - they touch no shared persona state.
[CollectionDefinition(FordUdsPersonaCollection.Name)]
public sealed class FordUdsPersonaCollection
{
    public const string Name = "FordUdsPersona";
}

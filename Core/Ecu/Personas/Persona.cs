using Core.Bus;

namespace Core.Ecu.Personas;

// Persona-shared helpers. Both Gmw3110Persona and UdsKernelPersona activate
// P3C the same way when a handler produces a positive response: store the
// channel as the most-recent enhanced channel and start (or reset) the
// tester-present timer. Lifting it here keeps the two persona dispatch
// tables free of bus plumbing.
internal static class Persona
{
    internal static void ActivateP3C(EcuNode node, ChannelSession ch)
    {
        node.State.LastEnhancedChannel = ch;
        node.State.TesterPresent.Activate();
    }
}

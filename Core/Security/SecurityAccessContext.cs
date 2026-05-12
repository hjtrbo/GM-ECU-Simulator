using Core.Bus;
using Core.Ecu;

namespace Core.Security;

// All the information a security module needs to process one $27 step.
// ref struct because UsdtPayload is a ReadOnlySpan<byte> that's only
// valid for the duration of the synchronous dispatch call — boxing the
// context into a closure that outlives Handle() would dangle the span.
public readonly ref struct SecurityAccessContext
{
    public required EcuNode Node { get; init; }
    public required ChannelSession Channel { get; init; }
    public required ReadOnlySpan<byte> UsdtPayload { get; init; }
    public required NodeState State { get; init; }
    public required long NowMs { get; init; }
    public required ISecurityEgress Egress { get; init; }
}

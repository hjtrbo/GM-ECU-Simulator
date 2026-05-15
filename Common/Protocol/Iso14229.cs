namespace Common.Protocol;

// ISO 14229-1:2020 (UDS) service identifiers. Sibling to Common.Protocol.Service,
// which holds the GMW3110-2010 table. The two are deliberately not merged - a
// handler that imports both blurs the spec-fidelity property the simulator
// protects, and several SIDs ($10, $22, $31, $34, $36, $3E) have overlapping
// values but different semantics between the specs.
//
// Only SIDs the simulator actually answers are listed; add new entries as
// kernel personas grow. NRC codes are shared with GMW3110 (both families
// inherit the KWP2000 NRC table), so use Common.Protocol.Nrc.* alongside.
public static class Iso14229
{
    public static class Service
    {
        public const byte EcuReset       = 0x11;   // §11.3 - reserved for the moment, NRC-only until a kernel needs it
        public const byte RoutineControl = 0x31;   // §11.7 - used by GM SPS programming kernels (Erase/Check by Address)
    }
}

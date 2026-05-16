using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// GMW3110 §8.16 SPS_TYPE_C activation flow. The DPS Get-Controller-Info
// session reproduced in the integration logs drives this state machine:
//   1. $28 functional   -> sets NormalCommunicationDisabled, no response.
//   2. $A2 functional   -> activates (DiagnosticResponsesEnabled = true) and
//                          replies on SPS_PrimeRsp ($300|addr).
//   3. Subsequent traffic on SPS_PrimeReq ($000|addr) hits the ECU like a
//      normal physical request, response on SPS_PrimeRsp.
//   4. $20 / P3C timeout -> diagnostic responses disabled, no $60 emitted
//      while in prime phase (§8.16 explicit carve-out).
public class SpsTypeCActivationTests
{
    private const byte DiagAddr = 0x11;
    private const ushort PrimeReq = 0x011;
    private const ushort PrimeRsp = 0x311;

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) WireTypeC()
    {
        var bus = new VirtualBus();
        var node = new EcuNode
        {
            Name = "Engine GMLAN $11",
            SpsType = SpsType.C,
            DiagnosticAddress = DiagAddr,
            PhysicalRequestCanId = PrimeReq,
            UsdtResponseCanId = PrimeRsp,
            UudtResponseCanId = (ushort)(0x500 | DiagAddr),
        };
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch);
    }

    private static byte[] AllNodesFrame(byte len, params byte[] payload)
    {
        var data = new byte[8];
        data[0] = GmlanCanId.AllNodesExtAddr;
        data[1] = len;
        Array.Copy(payload, 0, data, 2, System.Math.Min(payload.Length, 6));
        var frame = new byte[CanFrame.IdBytes + 8];
        CanFrame.WriteId(frame, GmlanCanId.AllNodesRequest);
        Array.Copy(data, 0, frame, CanFrame.IdBytes, 8);
        return frame;
    }

    private static byte[] PrimeReqFrame(byte len, params byte[] payload)
    {
        var data = new byte[8];
        data[0] = len;
        Array.Copy(payload, 0, data, 1, System.Math.Min(payload.Length, 7));
        var frame = new byte[CanFrame.IdBytes + 8];
        CanFrame.WriteId(frame, PrimeReq);
        Array.Copy(data, 0, frame, CanFrame.IdBytes, 8);
        return frame;
    }

    [Fact]
    public void Functional_A2_before_28_stays_silent()
    {
        // Spec: SPS_TYPE_C requires $28 active before $A2 activates the prime
        // phase. A standalone $A2 must not flip the gate and must not respond.
        var (_, node, ch) = WireTypeC();

        // bus.DispatchHostTx via the channel
        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.ReportProgrammedState), ch);

        Assert.False(node.State.DiagnosticResponsesEnabled);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Functional_28_does_not_respond_but_arms_for_A2()
    {
        // Spec: SPS_TYPE_C "shall not respond to any diagnostic request until
        // diagnostic responses are enabled" - so $28 functional updates state
        // (NormalCommunicationDisabled = true) without emitting $68.
        var (_, node, ch) = WireTypeC();

        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.DisableNormalCommunication), ch);

        Assert.True(node.State.NormalCommunicationDisabled);
        Assert.False(node.State.DiagnosticResponsesEnabled);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Functional_28_then_A2_activates_and_responds_on_SpsPrimeRsp()
    {
        // The full activation sequence: $28 arms, $A2 activates and replies on
        // SPS_PrimeRsp $311 (UsdtResponseCanId is configured = SPS_PrimeRsp,
        // so the normal response path produces the correct CAN ID).
        var (_, node, ch) = WireTypeC();

        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.DisableNormalCommunication), ch);
        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.ReportProgrammedState), ch);

        Assert.True(node.State.DiagnosticResponsesEnabled);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected $A2 response on SPS_PrimeRsp");
        Assert.Equal(PrimeRsp, CanFrame.ReadId(msg!.Data));
        Assert.Equal(new byte[] { 0xE2, 0x00 }, TestFrame.SingleFramePayload(msg.Data));
    }

    [Fact]
    public void Physical_request_before_activation_is_silently_dropped()
    {
        // The bus routes by PhysicalRequestCanId, which for SPS_TYPE_C is the
        // SPS_PrimeReq $011. The persona gate must ignore the request entirely
        // until the ECU has been activated.
        var (_, node, ch) = WireTypeC();

        ch.Bus!.DispatchHostTx(PrimeReqFrame(0x01, Service.ReportProgrammedState), ch);

        Assert.False(node.State.DiagnosticResponsesEnabled);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void After_activation_PrimeReq_routes_like_normal_physical()
    {
        // Post-activation, the ECU behaves like SPS_TYPE_A. A $A2 over
        // SPS_PrimeReq must produce a normal positive response.
        var (_, node, ch) = WireTypeC();
        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.DisableNormalCommunication), ch);
        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.ReportProgrammedState), ch);
        // Drain the activation $A2 reply.
        ch.RxQueue.TryDequeue(out _);

        ch.Bus!.DispatchHostTx(PrimeReqFrame(0x01, Service.ReportProgrammedState), ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected physical $A2 response");
        Assert.Equal(PrimeRsp, CanFrame.ReadId(msg!.Data));
        Assert.Equal(new byte[] { 0xE2, 0x00 }, TestFrame.SingleFramePayload(msg.Data));
    }

    [Fact]
    public void Functional_1A_B0_activates_and_replies_with_diag_address()
    {
        // DPS PM page 241: $1A $B0 is the "Return ECU Diagnostic Address"
        // probe used to re-discover ECUs after a programming session ends
        // and DiagnosticResponsesEnabled has been reset to false. The
        // silent-state SPS_TYPE_C dispatcher must answer it (and flip the
        // activation flag) so subsequent point-to-point requests like
        // $22 read can land on the now-known ECU.
        var (_, node, ch) = WireTypeC();
        Assert.False(node.State.DiagnosticResponsesEnabled);

        ch.Bus!.DispatchHostTx(AllNodesFrame(0x02, Service.ReadDataByIdentifier, 0xB0), ch);

        Assert.True(node.State.DiagnosticResponsesEnabled);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected $1A B0 response on SPS_PrimeRsp");
        Assert.Equal(PrimeRsp, CanFrame.ReadId(msg!.Data));
        Assert.Equal(new byte[] { 0x5A, 0xB0, DiagAddr }, TestFrame.SingleFramePayload(msg.Data));
    }

    [Fact]
    public void Functional_1A_OtherDid_StaysSilentAndDoesNotActivate()
    {
        // Only $B0 is special. A functional $1A read for any other DID on a
        // silent SPS_TYPE_C ECU must stay quiet AND must not flip the
        // activation gate - that gate is reserved for $28+$A2 or $1A B0.
        var (_, node, ch) = WireTypeC();

        ch.Bus!.DispatchHostTx(AllNodesFrame(0x02, Service.ReadDataByIdentifier, 0x90), ch);

        Assert.False(node.State.DiagnosticResponsesEnabled);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Exit_logic_clears_prime_phase_and_suppresses_20_response()
    {
        // §8.16: "An SPS_TYPE_C ECU shall not send a mode $20 response if it
        // receives a mode $20 request message or when a TesterPresent timeout
        // occurs during the phase when the SPS_PrimeReq and SPS_PrimeRsp CAN
        // identifiers are enabled." Verify both effects: no $60 enqueued and
        // DiagnosticResponsesEnabled reset to false.
        var (bus, node, ch) = WireTypeC();
        node.State.NormalCommunicationDisabled = true;
        node.State.DiagnosticResponsesEnabled = true;

        EcuExitLogic.Run(node, bus.Scheduler, ch);

        Assert.False(node.State.DiagnosticResponsesEnabled);
        Assert.False(node.State.NormalCommunicationDisabled);   // wiped by ClearProgrammingState
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Type_A_ECU_unaffected_by_new_gate()
    {
        // Regression: an SPS_TYPE_A (default) ECU still responds to $A2
        // functional unconditionally and goes through the normal handler.
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();   // SpsType.A default
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        ch.Bus!.DispatchHostTx(AllNodesFrame(0x01, Service.ReportProgrammedState), ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(NodeFactory.UsdtResp, CanFrame.ReadId(msg!.Data));
        Assert.Equal(new byte[] { 0xE2, 0x00 }, TestFrame.SingleFramePayload(msg.Data));
        Assert.False(node.State.DiagnosticResponsesEnabled);   // never set for Type A
    }
}

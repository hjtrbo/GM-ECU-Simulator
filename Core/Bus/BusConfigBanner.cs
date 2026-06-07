using Core.Ecu;

namespace Core.Bus;

// Emits a `#`-prefixed banner describing every ECU on the bus at log-start
// time. Lives at the top of every bus_*.csv so the configuration that
// produced the traffic is captured alongside it - if a host complains
// later, the FC bytes / security module / DID set in effect for that
// capture are right there next to the frames.
public static class BusConfigBanner
{
    public static IEnumerable<string> For(VirtualBus bus)
    {
        var nodes = bus.Nodes;
        yield return $"# Active configuration ({nodes.Count} ECU{(nodes.Count == 1 ? "" : "s")}):";

        if (nodes.Count == 0)
        {
            yield return "#   (no ECUs configured)";
            yield break;
        }

        foreach (var node in nodes)
        {
            foreach (var line in DescribeNode(node))
                yield return line;
        }
    }

    private static IEnumerable<string> DescribeNode(EcuNode node)
    {
        yield return $"#   [{node.Name}]{(node.IsPrimed ? " (primed)" : "")}";
        yield return $"#     CAN:           phys=0x{node.PhysicalRequestCanId:X3} usdtResp=0x{node.UsdtResponseCanId:X3} uudtResp=0x{node.UudtResponseCanId:X3} diagAddr=0x{node.DiagnosticAddress:X2}";

        // The FC frame the reassembler emits in response to an inbound FF.
        // STmin is hard-coded to 0 (the most-permissive value, see VirtualBus.DispatchHostTx).
        // BS is per-ECU - this is the knob that 6Speed.T43's `.Contains("01")` check needs.
        yield return $"#     FlowControl:   BS={node.FlowControlBlockSize} (FC frame = 30 {node.FlowControlBlockSize:X2} 00)";

        var securityId = node.SecurityModule?.Id ?? "(none)";
        var securityConfig = FormatSecurityConfig(node.SecurityModuleConfig);
        yield return $"#     Security:      module={securityId} config={securityConfig}";

        yield return $"#     Programmed:    state=0x{node.ProgrammedState:X2}";
        yield return $"#     Persona:       {node.Persona.GetType().Name}";
        yield return $"#     PIDs:          {node.AllPids.Count()}   DIDs: {node.Identifiers.Count}";

        // Ford UDS extras: flash bin backing Service $23 reads. Surfaced
        // here because a missing/wrong flashBinPath silently NRCs every $23
        // and PCMTec shows the user "Unknown Vehicle / CONDITIONS_NOT_CORRECT"
        // with no on-screen hint that the bin failed to load.
        if (node.Persona.Id == "ford-uds")
        {
            int size = Core.Ecu.Personas.FordUdsPersona.FlashBinSize;
            yield return size > 0
                ? $"#     FlashBin:      loaded, {size:N0} bytes (backs Service $23)"
                : $"#     FlashBin:      (NOT LOADED - Service $23 will NRC $22; set ecu.flashBinPath in config)";
        }
    }

    private static string FormatSecurityConfig(System.Text.Json.JsonElement? cfg)
    {
        if (cfg is null) return "(none)";
        var raw = cfg.Value.GetRawText();
        // Collapse whitespace so the whole config sits on one line in the banner.
        return string.Concat(raw.Where(c => c is not ('\r' or '\n' or '\t')));
    }
}

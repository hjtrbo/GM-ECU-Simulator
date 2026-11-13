using Core.Ecu;
using System.Text;

namespace Core.Dps;

// Dumps a human-readable snapshot of everything ArchivePrimer.ApplyTo
// produced: the new EcuNode's wire-visible config + identifiers + PIDs,
// plus the parsed archive contents that drove those choices. Written to
// LogDir (set by the app at startup) as a timestamped .txt so the user
// (or a bug report) can inspect the exact state DPS sees on the bus,
// without rummaging through JSON.
public static class PrimeReportWriter
{
    // Set by the app at startup. Falls back to %TEMP% if not assigned.
    public static string? LogDir { get; set; }

    public static string Write(EcuNode node, PrimedDataset dataset)
    {
        var dir = LogDir ?? Path.GetTempPath();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"GmEcuSimulator-prime-{stamp}.txt");
        File.WriteAllText(path, Build(node, dataset));
        return path;
    }

    public static string Build(EcuNode node, PrimedDataset dataset)
    {
        var r = dataset.Report;
        var sb = new StringBuilder();

        sb.AppendLine("=== GM ECU Simulator: Prime From Archive Report ===");
        sb.AppendLine($"Archive:    {r.ArchivePath}");
        sb.AppendLine($"Primed at:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(r.OneLineSummary());
        sb.AppendLine();

        WriteEcuSection(sb, node);
        WriteIdentifiersSection(sb, node);
        WritePidsSection(sb, node, dataset);
        WriteArchiveSection(sb, dataset);
        WriteCalBlocksSection(sb, dataset.Report);
        WriteUtilityFileSection(sb, dataset);
        WriteExpectedRequestsSection(sb, dataset);
        WriteBinIdentificationSection(sb, dataset);
        WriteSolverSection(sb, dataset);
        WriteFlagsSection(sb, r);

        return sb.ToString();
    }

    private static void WriteEcuSection(StringBuilder sb, EcuNode node)
    {
        sb.AppendLine("--- EcuNode ---");
        sb.AppendLine($"  Name:                  {node.Name}");
        sb.AppendLine($"  PhysicalRequestCanId:  0x{node.PhysicalRequestCanId:X3}");
        sb.AppendLine($"  UsdtResponseCanId:     0x{node.UsdtResponseCanId:X3}");
        sb.AppendLine($"  UudtResponseCanId:     0x{node.UudtResponseCanId:X3}");
        sb.AppendLine($"  DiagnosticAddress:     0x{node.DiagnosticAddress:X2}");
        sb.AppendLine($"  ProgrammedState:       0x{node.ProgrammedState:X2}");
        sb.AppendLine($"  Standard:              {node.PersonaId}");
        sb.AppendLine($"  SecurityModule:        {node.SecurityModule?.Id ?? "(none)"}");
        sb.AppendLine($"  DownloadAddrBytes:     {node.State.DownloadAddressByteCount}");
        sb.AppendLine($"  FlowControl BS:        {node.FlowControlBlockSize}");
        sb.AppendLine();
    }

    private static void WriteIdentifiersSection(StringBuilder sb, EcuNode node)
    {
        var ids = node.Identifiers;
        sb.AppendLine($"--- Identifier DIDs ({ids.Count} total) ---");
        if (ids.Count == 0)
        {
            sb.AppendLine("  (none)");
            sb.AppendLine();
            return;
        }

        foreach (var kv in ids.OrderBy(k => k.Key))
        {
            byte did = kv.Key;
            byte[] bytes = kv.Value;
            var source = node.GetIdentifierSource(did);
            string ascii = TryAscii(bytes);
            string hex = HexShort(bytes, 24);
            sb.AppendLine($"  0x{did:X2}  ({bytes.Length,3} bytes)  {hex,-72}  source={source}{ascii}");
        }
        sb.AppendLine();
    }

    private static void WritePidsSection(StringBuilder sb, EcuNode node, PrimedDataset dataset)
    {
        // Materialise the AllPids snapshot once - the rest of this method
        // counts and re-iterates it, and we want a stable view across both.
        var pids = node.AllPids.ToList();
        sb.AppendLine($"--- $22 PIDs registered ({pids.Count} total, from PidResponseSolver) ---");
        if (pids.Count == 0)
        {
            int scriptPidReads = dataset.UtilityFile.Instructions.Count(i => i.OpCode == 0x22);
            if (scriptPidReads == 0)
                sb.AppendLine("  (none - utility-file script has no $22 instructions; this archive's programming flow does not read PIDs)");
            else
                sb.AppendLine($"  (none - script has {scriptPidReads} $22 instruction(s) but solver produced no responses; check solver section)");
            sb.AppendLine();
            return;
        }

        foreach (var pid in pids.OrderBy(p => p.Address))
        {
            int len = pid.ResponseLength;
            string bytes = pid.StaticBytes is null ? "(waveform)" : HexShort(pid.StaticBytes, 24);
            sb.AppendLine($"  0x{pid.Address:X4}  ({len,3} bytes)  {bytes,-72}  {pid.Name}");
        }
        sb.AppendLine();
    }

    private static void WriteArchiveSection(StringBuilder sb, PrimedDataset dataset)
    {
        var r = dataset.Report;
        sb.AppendLine("--- Archive contents ---");
        sb.AppendLine($"  UtilityFile:           {r.UtilityFileName}");
        sb.AppendLine($"  Calibration files:     {r.CalFileCount}");
        sb.AppendLine($"  Donor boot block:      {r.DonorBinPath ?? "(none - archive-only prime)"}");
        sb.AppendLine($"  VIN:                   {r.Vin ?? "(none)"}");
        sb.AppendLine($"  VIN source:            {r.VinSource}");
        sb.AppendLine($"  Family:                {r.Family ?? "(unknown)"}");
        sb.AppendLine($"  OS PartNumber:         {r.OsPartNumber ?? "(not an archive OS module)"}");
        sb.AppendLine($"  OS AlphaCode:          {r.OsAlphaCode ?? "(n/a)"}");
        sb.AppendLine($"  Security module pick:  {r.SecurityModuleId}");
        if (r.SecuritySelection is { } selection)
        {
            sb.AppendLine($"  Utility security:      {selection.Summary}");
            foreach (var step in selection.Steps)
                sb.AppendLine($"    step {step.Step:X2}: AC={step.RawActions}, " +
                    $"algorithm={step.Algorithm:X2}, table={step.Table}, " +
                    $"seed/key modes={step.SeedMode:X2}/{step.KeyMode:X2}, " +
                    $"send key for zero seed={step.SendKeyForZeroSeed}; {step.Status}");
        }
        sb.AppendLine();
    }

    private static void WriteCalBlocksSection(StringBuilder sb, PrimeReport r)
    {
        var blocks = r.CalBlocks;
        sb.AppendLine($"--- Calibration layout ({blocks.Count} files) ---");
        if (blocks.Count == 0)
        {
            sb.AppendLine("  (none)");
            sb.AppendLine();
            return;
        }

        int nameWidth = Math.Max(blocks.Max(b => b.FileName.Length), 20);
        sb.AppendLine($"  {"File".PadRight(nameWidth)}  {"Bytes",12}  Start address");
        foreach (var b in blocks)
        {
            string addr = b.StartAddress.HasValue
                ? $"0x{b.StartAddress.Value:X8}"
                : "(unknown)";
            sb.AppendLine($"  {b.FileName.PadRight(nameWidth)}  {b.FileSizeBytes,12:N0}  {addr}");
        }
        sb.AppendLine();
    }

    private static void WriteUtilityFileSection(StringBuilder sb, PrimedDataset dataset)
    {
        var uf = dataset.UtilityFile;
        sb.AppendLine("--- Utility file ---");
        sb.AppendLine($"  PTI wrapper:           {(uf.Pti is null ? "none (bare SPS body)" : "present")}");
        sb.AppendLine($"  Interp type:           {uf.Sps.InterpType} (3=GMLAN)");
        sb.AppendLine($"  RoutineSectionOffset:  0x{uf.Sps.RoutineSectionOffset:X4}");
        sb.AppendLine($"  DataBytesPerMessage:   {uf.Sps.DataBytesPerMessage}");
        sb.AppendLine($"  Instructions:          {uf.Instructions.Count}");
        sb.AppendLine($"  Routines:              {uf.Routines.Count}");
        foreach (var rt in uf.Routines)
        {
            string preview = HexShort(rt.Data, 16);
            sb.AppendLine($"    routine[{rt.Index,2}]  addr=0x{rt.Address:X8}  len={rt.Data.Length,5}  {preview}");
        }
        sb.AppendLine();
    }

    private static void WriteExpectedRequestsSection(StringBuilder sb, PrimedDataset dataset)
    {
        var log = dataset.ExpectedRequests;
        sb.AppendLine($"--- ExpectedRequestLog ({log.Count} wire ops the script will issue) ---");
        foreach (var entry in log.Entries)
        {
            string action = string.Join(" ", entry.Action.Select(b => b.ToString("X2")));
            string opName = UtilityFileParser.OpCodeNames.TryGetValue(entry.OpCode, out var n) ? n : $"0x{entry.OpCode:X2}";
            sb.AppendLine($"  step 0x{entry.InstructionStep:X2}  ${entry.OpCode:X2}  {opName,-38}  action={action}");
        }
        sb.AppendLine();
    }

    private static void WriteBinIdentificationSection(StringBuilder sb, PrimedDataset dataset)
    {
        sb.AppendLine("--- Mode1ADidBinExtractor ---");
        var bi = dataset.BinIdentification;
        if (bi is null)
        {
            sb.AppendLine("  null (walker did not anchor - typically because the in-archive cal");
            sb.AppendLine("  module starts at flash 0x010000 rather than at 0x000000)");
            sb.AppendLine();
            return;
        }
        sb.AppendLine($"  Family:                {bi.Family}");
        sb.AppendLine($"  ServiceDispatcherOffset: 0x{bi.ServiceDispatcherOffset:X6}");
        sb.AppendLine($"  Service1A handler:       0x{bi.Service1AHandlerOffset:X6}");
        sb.AppendLine($"  Supported SIDs:        {string.Join(",", bi.SupportedSids.Select(s => $"${s:X2}"))}");
        sb.AppendLine($"  DIDs walked:           {bi.Dids.Count}");
        if (!string.IsNullOrEmpty(bi.Vin))           sb.AppendLine($"  VIN (regex):           {bi.Vin}");
        if (!string.IsNullOrEmpty(bi.SupplierHardwareNumber))  sb.AppendLine($"  SupplierHW#:           {bi.SupplierHardwareNumber}");
        if (!string.IsNullOrEmpty(bi.CalibrationPartNumber))   sb.AppendLine($"  CalPN:                 {bi.CalibrationPartNumber}");
        if (!string.IsNullOrEmpty(bi.BroadcastCode))           sb.AppendLine($"  BroadcastCode:         {bi.BroadcastCode}");
        if (!string.IsNullOrEmpty(bi.ProgrammingDate))         sb.AppendLine($"  ProgrammingDate:       {bi.ProgrammingDate}");
        if (!string.IsNullOrEmpty(bi.TraceCode))               sb.AppendLine($"  TraceCode:             {bi.TraceCode}");
        if (bi.Warnings.Count > 0)
        {
            sb.AppendLine("  Walker warnings:");
            foreach (var w in bi.Warnings) sb.AppendLine($"    - {w}");
        }
        sb.AppendLine();
    }

    private static void WriteSolverSection(StringBuilder sb, PrimedDataset dataset)
    {
        var s = dataset.SolverResult;
        sb.AppendLine("--- PidResponseSolver ---");
        sb.AppendLine($"  PIDs solved:           {s.Responses.Count}");
        sb.AppendLine($"  Satisfied compares:    {s.SatisfiedCompareCount}");
        sb.AppendLine($"  Unsatisfiable compares: {s.UnsatisfiableCompareCount}");
        sb.AppendLine($"  Known PIDs from bin:   {dataset.KnownPids.Count}");
        if (s.Responses.Count == 0 && dataset.KnownPids.Count > 0)
            sb.AppendLine($"  Note: {dataset.KnownPids.Count} PIDs extracted from OS bin but none are referenced by the utility-file script; DPS will not request them.");
        sb.AppendLine();
    }

    private static void WriteFlagsSection(StringBuilder sb, PrimeReport r)
    {
        sb.AppendLine($"--- Flags for review ({r.Flags.Count}) ---");
        if (r.Flags.Count == 0)
        {
            sb.AppendLine("  (none)");
            sb.AppendLine();
            return;
        }
        foreach (var f in r.Flags) sb.AppendLine($"  - {f}");
        sb.AppendLine();
    }

    private static string HexShort(byte[] bytes, int max)
    {
        if (bytes.Length == 0) return "(empty)";
        int n = Math.Min(bytes.Length, max);
        var hex = string.Join(" ", bytes.Take(n).Select(b => b.ToString("X2")));
        return bytes.Length > max ? hex + " ..." : hex;
    }

    private static string TryAscii(byte[] bytes)
    {
        // Only show ASCII suffix if every byte is printable.
        if (bytes.Length == 0) return "";
        foreach (var b in bytes)
            if (b < 0x20 || b > 0x7E) return "";
        return $"  ascii=\"{Encoding.ASCII.GetString(bytes)}\"";
    }
}

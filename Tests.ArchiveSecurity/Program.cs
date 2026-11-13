using System.Text.Json;
using Common.PassThru;
using Core.Bus;
using Core.Dps;
using Core.Security;

// Deterministic synthetic fixtures only; no adapter, IPC host, DLL loader,
// security key calculation, real firmware or captured identifiers.
var results = new List<object>();
int failures = 0;
void Check(string name, Action test)
{
    try { test(); results.Add(new { name, passed = true }); }
    catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.ToString() }); }
}
void Expect(bool predicate, string message)
{
    if (!predicate) throw new Exception(message);
}
UtilityFile Fixture(ushort interpreter = 3, params byte[][] actions)
    => new(null, new(0, 0, 0, 0, 0, interpreter, 24, 3, 0, 0),
        actions.Select((a, i) => new InterpreterInstruction((byte)(i + 1), 0x27, a, new byte[10])).ToArray(),
        Array.Empty<UtilityRoutine>());
UtilitySecurityStep Step(params byte[] a) => UtilitySecuritySelection.Inspect(Fixture(3, a)).Steps.Single();

Check("AC0 algorithm differs from AC1 access mode", () => {
    var s = Step(0x92, 0, 0, 0);
    Expect(s.Algorithm == 0x92 && s.SeedMode == 1 && s.KeyMode == 2 && s.Table == 0, "Wrong AC mapping");
});
Check("table1 and non-default access mode", () => {
    var s = Step(0x36, 1, 0x10, 1);
    Expect(s.Algorithm == 0x36 && s.Table == 1 && s.SeedMode == 3 && s.KeyMode == 4 && s.SendKeyForZeroSeed == true, "Selector lost");
});
Check("zero algorithm is metadata, not bypass", () => {
    var s = Step(0, 0, 0, 0);
    Expect(s.Algorithm == 0 && s.SendKeyForZeroSeed == false && s.Status.Contains("unresolved"), "False model binding");
});
Check("large access mode does not overflow", () => {
    var s = Step(1, 0x7D, 0, 0);
    Expect(s.SeedMode == 0xFB && s.KeyMode == 0xFC, "Access pair");
    s = Step(1, 0x7E, 0, 0);
    Expect(s.SeedMode == 0xFD && s.KeyMode == 0xFE, "Largest pair");
});
foreach (byte mode in new byte[] { 0x7F, 0x80, 0xFF })
    Check($"invalid mode {mode:X2} retained without truncation", () => {
        var s = Step(1, mode, 0, 0);
        Expect(s.SeedMode is null && s.KeyMode is null && s.RawActions == $"01{mode:X2}0000", "Truncated or lost selector");
    });
foreach (byte source in new byte[] { 1, 2, 3, 0x11, 0x20, 0xFF })
    Check($"unsupported source {source:X2} not masked into table", () => {
        var s = Step(1, 0, source, 0);
        Expect(s.Table is null && s.Status.StartsWith("Unsupported"), "Unsupported source guessed");
    });
Check("unsupported exception and algorithm remain unresolved", () => {
    Expect(Step(1, 0, 0, 2).SendKeyForZeroSeed is null, "Exception guessed");
    Expect(Step(0xFF, 0, 0, 0).Status.StartsWith("Unsupported"), "Algorithm FF accepted");
});
foreach (ushort interpreter in new ushort[] { 0, 1, 2, 4 })
    Check($"interpreter {interpreter} does not inherit GMLAN layout", () => {
        var s = UtilitySecuritySelection.Inspect(Fixture(interpreter, new byte[] { 0x92, 1, 0x10, 0 })).Steps.Single();
        Expect(s.Algorithm is null && s.Table is null && s.RawActions == "92011000", "Wrong interpreter mapping");
    });
Check("all security steps retained without choosing a branch", () => {
    var r = UtilitySecuritySelection.Inspect(Fixture(3, new byte[] {1,0,0,0}, new byte[] {2,1,0x10,0}));
    Expect(r.Steps.Count == 2 && r.Steps[1].Algorithm == 2, "Only first step retained");
});
Check("no security step never means unlocked", () => {
    var r = UtilitySecuritySelection.Inspect(Fixture());
    Expect(r.Steps.Count == 0 && r.Summary.Contains("disabled"), "Absent step bypassed");
});
foreach (byte[]? action in new byte[]?[] { null, new byte[3], new byte[5] })
    Check($"malformed action length {action?.Length.ToString() ?? "null"}", () => {
        var s = UtilitySecuritySelection.Inspect(Fixture(3, action!)).Steps.Single();
        Expect(s.Algorithm is null && s.Status.StartsWith("Malformed"), "Malformed action interpreted");
    });
Check("metadata is a snapshot of input bytes", () => {
    byte[] a = {1,0,0,0}; var r = UtilitySecuritySelection.Inspect(Fixture(3, a)); a[0] = 9;
    Expect(r.Steps[0].RawActions == "01000000" && r.Steps[0].Algorithm == 1, "Mutable source leaked");
});

foreach (byte[]? action in new byte[]?[] { new byte[] {0x92,0,0,0}, new byte[] {0,0,0,0}, null })
    Check($"synthetic archive import remains locked ({(action is null ? "no27" : Convert.ToHexString(action))})", () => {
        string dir = Path.Combine(Path.GetTempPath(), "archive-security-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            string zip = Path.Combine(dir, "synthetic.zip");
            SyntheticArchive.Create(zip, action);
            var dataset = ArchivePrimer.Prime(zip);
            Expect(dataset.Report.SecurityModuleId == UtilitySecuritySelection.UnresolvedModuleId, "Wrong imported module");
            Expect(dataset.Report.SecuritySelection is not null && dataset.Report.SecurityModuleConfig is null, "Metadata missing or cipher config fabricated");
            var node = ArchivePrimer.BuildEcuNode(dataset);
            var module = node.SecurityModule ?? throw new Exception("Unresolved module not registered");
            Expect(module.Behaviour == SecurityModuleBehaviour.Strict, "Imported module bypasses security");
            var egress = new CaptureEgress();
            var channel = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };
            foreach (byte[] request in new[] { new byte[] {0x27,1}, new byte[] {0x27,2,0,0}, new byte[] {0x27,3} }) {
                module.Handle(new SecurityAccessContext { Node = node, State = node.State, Channel = channel, UsdtPayload = request, Egress = egress, NowMs = 0 });
            }
            Expect(egress.Negative == 3 && egress.Positive == 0 && egress.Raw == 0 && egress.LastNrc == 0x11, "Exchange fabricated success");
            Expect(node.State.SecurityUnlockedLevel == 0 && node.State.SecurityModuleState is null, "Exchange changed security state");
            string report = PrimeReportWriter.Build(node, dataset);
            Expect(report.Contains("automatic access disabled"), "Report hides model limitation");
            if (action is not null) Expect(report.Contains("AC=" + Convert.ToHexString(action)), "Report loses raw selector");
            node.State.Fragmenter.Dispose();
        } finally { Directory.Delete(dir, true); }
    });

var output = JsonSerializer.Serialize(new { synthetic = true, hardware_access = false, tests = results.Count, failures, results }, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 1) { using var stream = new FileStream(args[0], FileMode.CreateNew); using var writer = new StreamWriter(stream); writer.WriteLine(output); }
Console.WriteLine(output);
return failures == 0 ? 0 : 1;

sealed class CaptureEgress : ISecurityEgress
{
    public int Positive, Negative, Raw;
    public byte LastNrc;
    public void SendPositiveResponse(byte subfunction, ReadOnlySpan<byte> data) => Positive++;
    public void SendNegativeResponse(byte nrc) { Negative++; LastNrc = nrc; }
    public void SendRaw(ReadOnlySpan<byte> payload) => Raw++;
}

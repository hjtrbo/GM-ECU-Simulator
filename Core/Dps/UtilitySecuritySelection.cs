namespace Core.Dps;

// Static metadata only. GM SPS Interpreter manual, 2011-04-01, pp. 163-164:
// interpreter 3 opcode 27 uses AC0 for algorithm, AC1 for access mode,
// AC2 for algorithm source/table and AC3 for the zero-seed exception.
// https://www.scribd.com/doc/85336952/Interpreters-April-01-2011
// Neither a selector nor a filename establishes an ECU family or seed width.
public sealed record UtilitySecurityStep(
    byte Step, string RawActions, byte? Algorithm, byte? Table,
    byte? SeedMode, byte? KeyMode, bool? SendKeyForZeroSeed, string Status);

public sealed record UtilitySecuritySelection(
    ushort Interpreter, IReadOnlyList<UtilitySecurityStep> Steps, string Summary)
{
    public const string UnresolvedModuleId = "gm-archive-unresolved";

    public static UtilitySecuritySelection Inspect(UtilityFile utility)
    {
        var steps = new List<UtilitySecurityStep>();
        foreach (var instruction in utility.Instructions)
        {
            if (instruction.OpCode != 0x27) continue;
            var action = instruction.Action;
            string raw = action is null ? "(missing)" : Convert.ToHexString(action);
            if (action is null || action.Length != 4 || utility.Sps.InterpType != 3)
            {
                steps.Add(new(instruction.Step, raw, null, null, null, null, null,
                    action is null || action.Length != 4
                        ? "Malformed action fields; selector unresolved."
                        : "Interpreter not supported by this selector decoder."));
                continue;
            }

            // Decode each independently so unsupported sources still retain the
            // raw selector. Do not collapse multiple steps or assume reachability.
            byte? table = action[2] switch { 0x00 => 0, 0x10 => 1, _ => null };
            bool modeFits = action[1] <= 0x7E;
            byte? seedMode = modeFits ? (byte)(2 * action[1] + 1) : null;
            byte? keyMode = modeFits ? (byte)(2 * action[1] + 2) : null;
            bool? zeroSeed = action[3] switch { 0 => false, 1 => true, _ => null };
            bool decoded = table.HasValue && modeFits && zeroSeed.HasValue && action[0] != 0xFF;
            steps.Add(new(instruction.Step, raw, action[0], table, seedMode,
                keyMode, zeroSeed, decoded
                    ? "Selector decoded; security implementation and target binding unresolved."
                    : "Unsupported selector fields; raw actions retained."));
        }

        return new(utility.Sps.InterpType, steps.AsReadOnly(), steps.Count == 0
            ? "No security instruction found. No security model inferred; automatic access disabled."
            : "Security instructions inspected without control-flow execution. No verified model binding; automatic access disabled.");
    }
}

namespace Core.Ecu;

// Dynamic Packet Identifier — a tester-defined bundle of PIDs that can be
// pushed back as one UUDT frame at periodic rates. Defined via $2C; read via
// $AA. Total of all PIDs' value bytes must fit in 7 bytes (UUDT frame minus
// the leading DPID id byte).
public sealed class Dpid
{
    public required byte Id { get; init; }
    public required IReadOnlyList<Pid> Pids { get; init; }
}

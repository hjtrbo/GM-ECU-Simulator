namespace Core.Ecu;

// One contiguous flash region erased by the SPS kernel via $31 RoutineControl
// (routineId $FF00 EraseMemoryByAddress). Lives in NodeState so subsequent
// $36 TransferData writes that land inside [StartAddress, StartAddress+Size)
// can mirror their data into <see cref="Buffer"/>. At session end the buffer
// is written out as one consolidated .bin per region by
// <see cref="Core.Services.BootloaderCaptureWriter"/> - so the captures dir
// contains real flash images (one per erased region, sized to the kernel's
// declared erase length) in addition to the per-$36 fragment files.
//
// The buffer is initialised to $FF to match real NOR flash post-erase. Any
// byte the kernel doesn't subsequently overwrite via $36 stays at $FF in
// the captured image, which is the on-device reality the user is trying
// to mirror.
public sealed class FlashEraseRegion
{
    public uint StartAddress { get; }
    public uint Size { get; }

    /// <summary>Erased-flash backing store, $FF-filled at construction.</summary>
    public byte[] Buffer { get; }

    /// <summary>Total dataRecord bytes that $36 has mirrored into this region.</summary>
    public uint BytesWritten { get; set; }

    public FlashEraseRegion(uint startAddress, uint size)
    {
        StartAddress = startAddress;
        Size = size;
        Buffer = new byte[size];
        Buffer.AsSpan().Fill(0xFF);
    }

    /// <summary>True if <paramref name="address"/> falls inside this region.</summary>
    public bool Contains(uint address) =>
        address >= StartAddress && address < StartAddress + Size;
}

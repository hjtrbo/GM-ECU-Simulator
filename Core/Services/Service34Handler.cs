using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $34 RequestDownload. GMW3110-2010 §8.12 (p156-160).
//
// Request: SID $34 + dataFormatIdentifier (1 byte) + unCompressedMemorySize (2..4 bytes).
//   dataFormatIdentifier: high nibble = compressionMethod, low nibble = encryptingMethod.
//                         $00 means no compression, no encryption (the only case we accept).
//   unCompressedMemorySize: 2-, 3- or 4-byte big-endian unsigned size.
// Positive response: $74 (no data parameters).
//
// NRCs (§8.12.4, Table 135):
//   $12 SFNS-IF        - length wrong, dataFormatIdentifier unsupported, or memorySize invalid
//   $22 CNCRSE         - $28 not active, $A5 not active, security locked, or download in progress
//   $78 RCR-RP         - cannot process within P2C (we never need this)
//   $99 ReadyForDownload-DTCStored - flash/EEPROM checksum DTC set (we never set this)
//
// We accept dataFormatIdentifier $00 only. Encryption / compression are vendor-
// specific and out of scope for the simulator; a tester that wants to round-trip
// a payload should use the no-compression / no-encryption form.
public static class Service34Handler
{
    /// <summary>
    /// Defensive upper bound on the buffer this simulator will allocate for an
    /// incoming download. 16 MiB covers every realistic GMW3110 ECU calibration
    /// + bootloader image; rejecting larger sizes prevents a malformed (or
    /// hostile) host from triggering a multi-GB allocation. GMW3110 doesn't
    /// have a dedicated NRC for "buffer too large", so we map the violation to
    /// $22 ConditionsNotCorrect (which §8.12.4 lists generically for "ECU not
    /// in a state to accept this request").
    /// </summary>
    public const uint MaxDownloadBufferBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Returns true if a positive response was sent (caller activates P3C),
    /// false if an NRC was sent.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        // Total length: 1 SID + 1 dataFormatIdentifier + (2..4) memorySize bytes = 4..6 bytes.
        if (usdtPayload.Length < 4 || usdtPayload.Length > 6 || usdtPayload[0] != Service.RequestDownload)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte dataFormatIdentifier = usdtPayload[1];
        if (dataFormatIdentifier != 0x00)
        {
            // §8.12.4 NRC $12: dataFormatIdentifier value is not supported by the node.
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // Decode the unCompressedMemorySize - the remaining 2..4 bytes BE.
        int sizeByteCount = usdtPayload.Length - 2;
        uint declaredSize = 0;
        for (int i = 0; i < sizeByteCount; i++)
            declaredSize = (declaredSize << 8) | usdtPayload[2 + i];

        if (declaredSize == 0)
        {
            // Zero-byte download is invalid per the implicit contract that $36 must
            // transfer at least one data byte.
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        if (declaredSize > MaxDownloadBufferBytes)
        {
            // Over the simulator's resource cap; reject with $22 CNCRSE so the
            // host knows the ECU can't process this size right now (vs $12
            // which implies the value is structurally invalid).
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // §8.12.4 NRC $22 CNCRSE preconditions.
        if (!node.State.NormalCommunicationDisabled
            || !node.State.ProgrammingModeActive
            || node.State.SecurityUnlockedLevel == 0
            || node.State.DownloadActive)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // Allocate the sink buffer. The address-byte-count for subsequent $36s
        // is fixed once $34 has been accepted; the spec (§8.13.2 Note 1) says
        // all $36 to the same node use the same number of address bytes, so we
        // freeze it here. Default 3 bytes (covers a 16 MiB address space).
        node.State.DownloadDeclaredSize = declaredSize;
        node.State.DownloadBuffer = new byte[declaredSize];
        node.State.DownloadBytesReceived = 0;
        node.State.DownloadActive = true;

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.RequestDownload)]);
        return true;
    }
}

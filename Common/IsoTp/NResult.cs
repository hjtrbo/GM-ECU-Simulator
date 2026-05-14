namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 §8.3.7 N_Result. Status returned to the upper protocol
/// layer when an N_USData service completes (success) or fails (error).
///
/// The order matters: §8.3.7 mandates that "if two or more errors are
/// discovered at the same time, then the network layer entity shall use the
/// parameter value found first in this list when indicating the error to the
/// higher layers". The enum is laid out in the spec's listed order so a
/// numeric compare resolves precedence correctly (lower wins).
/// </summary>
public enum NResult
{
    /// <summary>Service completed successfully (sender or receiver).</summary>
    N_OK = 0,

    /// <summary>N_As (sender) or N_Ar (receiver) timed out at the data-link layer.</summary>
    N_TIMEOUT_A = 1,

    /// <summary>Sender did not receive an FC N_PDU within N_Bs.</summary>
    N_TIMEOUT_Bs = 2,

    /// <summary>Receiver did not see the next CF within N_Cr.</summary>
    N_TIMEOUT_Cr = 3,

    /// <summary>Receiver got a CF with an unexpected SequenceNumber (§9.6.4.4).</summary>
    N_WRONG_SN = 4,

    /// <summary>Sender got an FC with an invalid (reserved) FlowStatus (§9.6.5.2).</summary>
    N_INVALID_FS = 5,

    /// <summary>Receiver got an N_PDU outside the expected order (§9.8.3).</summary>
    N_UNEXP_PDU = 6,

    /// <summary>Receiver sent N_WFTmax FC.WAIT frames without making progress (§9.8.4).</summary>
    N_WFT_OVRN = 7,

    /// <summary>Sender got an FC with FS = OVFLW; FF_DL exceeded receiver buffer (§9.6.3.2 / §9.6.5.1).</summary>
    N_BUFFER_OVFLW = 8,

    /// <summary>Generic network-layer error not covered by the more specific codes.</summary>
    N_ERROR = 9,
}

using System;
using System.Text;
using Common.Protocol;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

/// <summary>
/// One row in the ECU's Identifiers grid. Wraps a single ($1A DID -> value)
/// entry on <see cref="EcuNode.Identifiers"/>. The Value text is dual-format:
/// a string prefixed with "0x" or containing only hex pairs/separators is
/// parsed as raw bytes; anything else is stored as ASCII. This matches the
/// existing IdentifierDto.Ascii / IdentifierDto.Hex split in the persisted
/// config (the loader prefers Ascii when both are non-null).
///
/// The row pushes changes straight to <see cref="EcuNode.SetIdentifier"/> on
/// every edit. Removing the row from the parent's collection calls
/// <see cref="EcuNode.RemoveIdentifier"/>; the parent VM owns that wiring.
/// </summary>
public sealed class IdentifierRowViewModel : NotifyPropertyChangedBase
{
    private readonly EcuNode model;
    private byte did;
    private string value;
    // Initial format inferred from the bytes (printable ASCII -> Text mode,
    // anything else -> Hex). The user can flip the toggle to override; the
    // value text is converted across formats so a half-typed entry doesn't
    // get garbled.
    private bool isHex;

    public IdentifierRowViewModel(EcuNode model, byte did, byte[] bytes)
    {
        this.model = model;
        this.did = did;
        if (IdentifierValueParser.IsPrintableAscii(bytes))
        {
            isHex = false;
            value = Encoding.ASCII.GetString(bytes);
        }
        else
        {
            isHex = true;
            value = IdentifierValueParser.ToHexString(bytes);
        }
    }

    /// <summary>Raw DID byte. UI binds to <see cref="DidHex"/> for display.</summary>
    public byte Did
    {
        get => did;
        private set
        {
            if (did == value) return;
            did = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DidHex));
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>$XX display + edit. Editing rewrites the DID on the model:
    /// the old DID is removed and the current value is re-stored under the
    /// new DID. No-op if the new DID is malformed or unchanged.</summary>
    public string DidHex
    {
        get => $"${did:X2}";
        set
        {
            if (!IdentifierValueParser.TryParseHexByte(value, out var newDid)) return;
            if (newDid == did) return;
            // Move the value to the new DID.
            model.RemoveIdentifier(did);
            Did = newDid;
            PushValue();
        }
    }

    /// <summary>Friendly label from <see cref="Gmw3110DidNames"/>. Read-only.</summary>
    public string Name => Gmw3110DidNames.NameOf(did) ?? "";

    /// <summary>
    /// Provenance of the current value: "user" (typed in the grid, sticky
    /// even after the value is blanked), "bin" (Load Info From Bin), "auto"
    /// (Auto-populate), or "-" (DidSource.Blank - never touched). Read-only
    /// display; the underlying source is set on the model by whichever writer
    /// wrote the value. Refreshed on every <see cref="PushValue"/> so a user
    /// edit flips the column from auto/bin to user as soon as focus leaves
    /// the row.
    /// </summary>
    public string SourceLabel => model.GetIdentifierSource(did) switch
    {
        DidSource.User => "user",
        DidSource.Bin  => "bin",
        DidSource.Auto => "auto",
        _              => "-",
    };

    /// <summary>
    /// Value text. Interpretation depends on <see cref="IsHex"/>:
    ///   - false (Text): stored as ASCII bytes; an empty string clears the DID.
    ///   - true  (Hex):  parsed as space-or-comma-separated hex byte list,
    ///                   optional "0x" prefix per token; invalid hex is ignored
    ///                   (the previous bytes stay until the user types valid hex).
    /// </summary>
    public string Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value ?? "";
            PushValue();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Toggle between text and hex display. Flipping converts the current
    /// bytes to the new representation so the user doesn't lose their typed
    /// value. Empty values stay empty across the toggle.
    /// </summary>
    public bool IsHex
    {
        get => isHex;
        set
        {
            if (isHex == value) return;
            // Convert the current displayed value to the new format using the
            // current bytes as source-of-truth - that way garbage hex input
            // doesn't survive a toggle round-trip.
            var bytes = model.GetIdentifier(did) ?? Array.Empty<byte>();
            isHex = value;
            this.value = isHex
                ? IdentifierValueParser.ToHexString(bytes)
                : (IdentifierValueParser.IsPrintableAscii(bytes) ? Encoding.ASCII.GetString(bytes) : "");
            OnPropertyChanged();
            OnPropertyChanged(nameof(Value));
        }
    }

    private void PushValue()
    {
        // Every grid-driven write or blank flips the source to User. The model's
        // SetIdentifierSource is sticky across subsequent RemoveIdentifier calls,
        // so even a deliberate blank stays User until something else (e.g. a
        // Replace-all bin load) re-tags the row.
        if (string.IsNullOrEmpty(value))
        {
            model.RemoveIdentifier(did);
            model.SetIdentifierSource(did, DidSource.User);
            OnPropertyChanged(nameof(SourceLabel));
            return;
        }
        byte[]? bytes = isHex ? IdentifierValueParser.TryParseHexBytes(value) : Encoding.ASCII.GetBytes(value);
        if (bytes == null) return;     // invalid hex - keep old bytes in the model
        model.SetIdentifier(did, bytes, DidSource.User);
        OnPropertyChanged(nameof(SourceLabel));
    }
}

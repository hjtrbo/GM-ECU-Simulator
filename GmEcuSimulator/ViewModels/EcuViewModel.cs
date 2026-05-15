using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Identification;
using Core.Scheduler;
using Core.Security;
using Core.Services;
using Microsoft.Win32;

namespace GmEcuSimulator.ViewModels;

// Editable view of one EcuNode plus its child PidViewModels. CAN ID
// edits push straight to the model; on the next IPC dispatch they take
// effect (the bus's FindByRequestId does the lookup fresh per frame).
public sealed class EcuViewModel : NotifyPropertyChangedBase
{
    public EcuNode Model { get; }
    public ObservableCollection<PidViewModel> Pids { get; } = new();
    public GlitchConfigViewModel Glitch { get; }
    private PidViewModel? selectedPid;

    public EcuViewModel(EcuNode model)
    {
        Model = model;
        Glitch = new GlitchConfigViewModel(model.Glitch);
        foreach (var pid in model.Pids) Pids.Add(new PidViewModel(pid, this));

        // Security module picker: synthetic "(none)" at index 0, then every
        // registered module ID. Matches the ComboBox's ItemsSource binding.
        AvailableSecurityModuleIds = new ObservableCollection<string> { NoneSecurityModuleLabel };
        foreach (var id in SecurityModuleRegistry.KnownIds) AvailableSecurityModuleIds.Add(id);
        selectedSecurityModuleId = model.SecurityModule?.Id ?? NoneSecurityModuleLabel;

        // Initial KV entries from any persisted config.
        SecurityModuleConfigEntries = new ObservableCollection<KeyValueEntry>();
        LoadEntriesFromJson(model.SecurityModuleConfig);
        SecurityModuleConfigEntries.CollectionChanged += OnSecurityEntriesChanged;
        foreach (var e in SecurityModuleConfigEntries) e.PropertyChanged += OnSecurityEntryPropertyChanged;

        LoadInfoFromBinCommand = new RelayCommand(LoadInfoFromBin);

        // Validate any pre-loaded DID values so the red border appears on
        // open if the persisted config has invalid input (e.g. a 16-char
        // VIN from before the constraint existed).
        Validate(nameof(Vin), Vin, EcuFieldValidators.ValidateVin);
        Validate(nameof(SupplierHardwareNumber), SupplierHardwareNumber, EcuFieldValidators.ValidateSupplierAscii);
        Validate(nameof(SupplierHardwareVersion), SupplierHardwareVersion, EcuFieldValidators.ValidateSupplierAscii);
        Validate(nameof(EndModelPartNumber), EndModelPartNumber, EcuFieldValidators.Validate4ByteHexBE);
        Validate(nameof(BaseModelPartNumber), BaseModelPartNumber, EcuFieldValidators.ValidatePartNumber);
        Validate(nameof(EcuDiagnosticAddress4ByteHex), EcuDiagnosticAddress4ByteHex, EcuFieldValidators.Validate4ByteHexBE);
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); } }
    }

    public ushort PhysicalRequestCanId
    {
        get => Model.PhysicalRequestCanId;
        set { if (Model.PhysicalRequestCanId != value) { Model.PhysicalRequestCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(PhysicalRequestCanIdHex)); } }
    }

    public string PhysicalRequestCanIdHex
    {
        get => $"0x{Model.PhysicalRequestCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) PhysicalRequestCanId = v; }
    }

    public ushort UsdtResponseCanId
    {
        get => Model.UsdtResponseCanId;
        set { if (Model.UsdtResponseCanId != value) { Model.UsdtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsdtResponseCanIdHex)); } }
    }

    public string UsdtResponseCanIdHex
    {
        get => $"0x{Model.UsdtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UsdtResponseCanId = v; }
    }

    public ushort UudtResponseCanId
    {
        get => Model.UudtResponseCanId;
        set { if (Model.UudtResponseCanId != value) { Model.UudtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UudtResponseCanIdHex)); } }
    }

    public string UudtResponseCanIdHex
    {
        get => $"0x{Model.UudtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UudtResponseCanId = v; }
    }

    // ---------------- GMW3110 §8.3.2 ECU identity DIDs ----------------
    //
    // Five well-known ReadDataByIdentifier ($1A) values surfaced as first-
    // class textboxes in the inspector. All values round-trip through the
    // generic Identifiers map on EcuNode, so persistence is handled by the
    // existing IdentifierDto round-trip in ConfigStore - no schema change
    // required, and these DIDs also serve $1A on the wire automatically.
    //
    // Display semantics:
    //   $90 / $92 / $98 / $C2  - ASCII strings (printable).
    //   $C1 / $CC              - 4-byte BE hex ("0x017240DB"); the on-the-
    //                            wire response for these DIDs is 4 raw bytes
    //                            (the real ECU reads them straight from flash
    //                            or a RAM cache), so the editor surfaces them
    //                            as hex rather than ASCII.
    //
    // Empty/whitespace input removes the DID from the map so $1A goes back
    // to its spec-correct NRC $31 RequestOutOfRange rather than echoing an
    // empty value.

    // Validation note: each setter writes the user's value into the model
    // *before* validating, then runs the validator and stashes the result
    // via SetError(). This lets the user type partial input ("3 chars and
    // counting...") without the value being silently dropped - the red
    // border just appears until the value is complete and correct. Empty
    // input is treated as "clear the DID" and is always valid, matching
    // the existing setters' behaviour and the simulator's $1A NRC $31
    // response for unknown DIDs.

    /// <summary>DID $90 - Vehicle Identification Number (17 ASCII bytes per spec).</summary>
    public string Vin
    {
        get => GetAsciiDid(0x90);
        set
        {
            if (SetAsciiDid(0x90, value)) OnPropertyChanged();
            Validate(nameof(Vin), value, EcuFieldValidators.ValidateVin);
        }
    }

    /// <summary>DID $92 - System Supplier ECU Hardware Number (ASCII).</summary>
    public string SupplierHardwareNumber
    {
        get => GetAsciiDid(0x92);
        set
        {
            if (SetAsciiDid(0x92, value)) OnPropertyChanged();
            Validate(nameof(SupplierHardwareNumber), value, EcuFieldValidators.ValidateSupplierAscii);
        }
    }

    /// <summary>DID $98 - System Supplier ECU Hardware Version Number (ASCII).</summary>
    public string SupplierHardwareVersion
    {
        get => GetAsciiDid(0x98);
        set
        {
            if (SetAsciiDid(0x98, value)) OnPropertyChanged();
            Validate(nameof(SupplierHardwareVersion), value, EcuFieldValidators.ValidateSupplierAscii);
        }
    }

    /// <summary>DID $C1 - End Model Part Number Identification (4 bytes BE hex, e.g. "0x017240DB").</summary>
    public string EndModelPartNumber
    {
        get => Get4ByteHexDid(0xC1);
        set
        {
            if (Set4ByteHexDid(0xC1, value)) OnPropertyChanged();
            Validate(nameof(EndModelPartNumber), value, EcuFieldValidators.Validate4ByteHexBE);
        }
    }

    /// <summary>DID $C2 - Base Model Part Number Identification (ASCII).</summary>
    public string BaseModelPartNumber
    {
        get => GetAsciiDid(0xC2);
        set
        {
            if (SetAsciiDid(0xC2, value)) OnPropertyChanged();
            Validate(nameof(BaseModelPartNumber), value, EcuFieldValidators.ValidatePartNumber);
        }
    }

    /// <summary>DID $CC - ECU Diagnostic Address (4 bytes BE hex, e.g. "0x00000018").</summary>
    public string EcuDiagnosticAddress4ByteHex
    {
        get => Get4ByteHexDid(0xCC);
        set
        {
            if (Set4ByteHexDid(0xCC, value)) OnPropertyChanged();
            Validate(nameof(EcuDiagnosticAddress4ByteHex), value, EcuFieldValidators.Validate4ByteHexBE);
        }
    }

    // -------- EEPROM-block informational fields (Stage 2 segment reader) --------
    //
    // The fields below come out of the bin's EEPROM_DATA segment via the
    // SegmentReader. They aren't on the GMW3110 $1A wire path, so they
    // don't round-trip through EcuNode.Identifiers - they're display-only
    // ViewModel state that the "Load Info From Bin..." button populates.
    // (They don't survive simulator restarts; persistence wiring can come
    // later when there's a use case for it.)

    private string broadcastCode = "";
    /// <summary>EEPROM "BCC" field - the 4-char broadcast code (last 4 chars of the calibration PN).</summary>
    public string BroadcastCode
    {
        get => broadcastCode;
        set
        {
            if (SetField(ref broadcastCode, value ?? ""))
                Validate(nameof(BroadcastCode), value, EcuFieldValidators.ValidateBroadcastCode);
        }
    }

    private string programmingDate = "";
    /// <summary>EEPROM programming-date stamp, decoded from 4 BCD bytes to YYYYMMDD.</summary>
    public string ProgrammingDate
    {
        get => programmingDate;
        set
        {
            if (SetField(ref programmingDate, value ?? ""))
                Validate(nameof(ProgrammingDate), value, EcuFieldValidators.ValidateProgrammingDate);
        }
    }

    private string traceCode = "";
    /// <summary>Bosch/Delphi supplier trace stamp - 16 chars on T43, 16 chars on E38/E67.</summary>
    public string TraceCode
    {
        get => traceCode;
        set
        {
            if (SetField(ref traceCode, value ?? ""))
                Validate(nameof(TraceCode), value, EcuFieldValidators.ValidateSupplierAscii);
        }
    }

    private string calibrationPartNumber = "";
    /// <summary>
    /// EEPROM "PCM" field - calibration broadcast part number as decimal.
    /// Different from $C1 (the service end-model PN) - this is the cal-ID
    /// that GM tuners refer to when they talk about "the OS" or "the
    /// PCM number" of a flash.
    /// </summary>
    public string CalibrationPartNumber
    {
        get => calibrationPartNumber;
        set
        {
            if (SetField(ref calibrationPartNumber, value ?? ""))
                Validate(nameof(CalibrationPartNumber), value, EcuFieldValidators.ValidatePartNumber);
        }
    }

    /// <summary>
    /// "Load Info From Bin" button command. Pops a file picker, parses the
    /// selected .bin via <see cref="BinIdentificationReader"/>, and pushes the
    /// extracted identity fields into the inspector textboxes. Skips any
    /// field the parser couldn't resolve (so the user can fill blanks
    /// manually) and only overwrites existing values when the parser found
    /// something new - keeps the operation idempotent across re-loads.
    /// </summary>
    public RelayCommand LoadInfoFromBinCommand { get; }

    private void LoadInfoFromBin()
    {
        var picker = new OpenFileDialog
        {
            Title = "Pick a GM ECU flash image",
            Filter = "ECU bin (*.bin)|*.bin|All files|*.*",
        };
        if (picker.ShowDialog() != true) return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "Load Info From Bin",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var result = BinIdentificationReader.Parse(bytes);
        if (result == null)
        {
            MessageBox.Show(
                "Could not identify this file as a GM ECU flash image. No service " +
                "dispatcher was located - file may be truncated, encrypted, or a " +
                "different ECU family than T43/E38/E67.",
                "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Apply extracted fields. Empty/null values fall through to no-op
        // setters (the property setters already handle "no change" / "blank
        // clears" semantics), so we don't need conditional logic per field.
        var applied = new List<string>();
        if (!string.IsNullOrEmpty(result.Vin)) { Vin = result.Vin!; applied.Add("$90 VIN"); }
        if (!string.IsNullOrEmpty(result.SupplierHardwareNumber))
            { SupplierHardwareNumber = result.SupplierHardwareNumber!; applied.Add("$92 HW#"); }
        if (!string.IsNullOrEmpty(result.SupplierHardwareVersion))
            { SupplierHardwareVersion = result.SupplierHardwareVersion!; applied.Add("$98 HW Ver"); }
        // $C1 is a 4-byte BE field on the wire. When the parser traced it
        // through the $1A handler (FlashUInt32BE), prefer the raw WireBytes
        // over the decimal-ASCII fallback so what we show matches what the
        // real ECU would return on the bus.
        var c1 = result.FindDid(0xC1);
        if (c1 != null && c1.Kind == BinIdentificationReader.DidSourceKind.FlashUInt32BE
                       && c1.WireBytes.Length == 4)
        {
            EndModelPartNumber = "0x" + Convert.ToHexString(c1.WireBytes);
            applied.Add("$C1 End P/N");
        }
        if (!string.IsNullOrEmpty(result.BaseModelPartNumber))
            { BaseModelPartNumber = result.BaseModelPartNumber!; applied.Add("$C2 Base P/N"); }

        // Stage 2: EEPROM-block informational fields. Display-only state;
        // not pushed to Model.Identifiers because these aren't on the $1A
        // wire path of any family we've inspected.
        if (!string.IsNullOrEmpty(result.CalibrationPartNumber))
            { CalibrationPartNumber = result.CalibrationPartNumber!; applied.Add("Cal P/N"); }
        if (!string.IsNullOrEmpty(result.BroadcastCode))
            { BroadcastCode = result.BroadcastCode!; applied.Add("BCC"); }
        if (!string.IsNullOrEmpty(result.ProgrammingDate))
            { ProgrammingDate = result.ProgrammingDate!; applied.Add("ProgDate"); }
        if (!string.IsNullOrEmpty(result.TraceCode))
            { TraceCode = result.TraceCode!; applied.Add("Trace"); }

        // Compose a summary message - shows which fields were populated and
        // surfaces any parser warnings (e.g. "no trampoline pattern detected").
        var lines = new List<string>
        {
            $"Family: {result.Family}",
            $"Service dispatcher: 0x{result.ServiceDispatcherOffset:X6}",
            $"$1A handler: 0x{result.Service1AHandlerOffset:X6}",
            $"DID dispatcher: 0x{result.DidDispatcherOffset:X6}",
            $"Supported SIDs: {string.Join(", ", result.SupportedSids.Select(s => $"${s:X2}"))}",
            $"Supported DIDs: {string.Join(", ", result.Dids.Select(s => $"${s.Did:X2}"))}",
            "",
            applied.Count > 0
                ? $"Populated: {string.Join(", ", applied)}"
                : "No fields populated (no extractable values found).",
        };
        if (result.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings:");
            foreach (var w in result.Warnings) lines.Add("  - " + w);
        }
        MessageBox.Show(string.Join(Environment.NewLine, lines),
            "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string GetAsciiDid(byte did)
    {
        var bytes = Model.GetIdentifier(did);
        return bytes is { Length: > 0 } ? System.Text.Encoding.ASCII.GetString(bytes) : "";
    }

    private bool SetAsciiDid(byte did, string? value)
    {
        var s = value ?? "";
        if (s.Length == 0) return Model.RemoveIdentifier(did);
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        var current = Model.GetIdentifier(did);
        if (current != null && current.AsSpan().SequenceEqual(bytes)) return false;
        Model.SetIdentifier(did, bytes);
        return true;
    }

    // Shared getter/setter for DIDs whose wire format is 4 raw bytes BE (the
    // real ECU reads a uint32 from flash or RAM). Display is uppercase
    // "0x" + 8 hex digits; blank clears the DID. Storage that isn't exactly
    // 4 bytes (e.g. an older config that stashed $C1 as ASCII digits) reads
    // back as empty so the inspector doesn't crash or show garbage - the
    // user can re-enter or re-run Load Info From Bin to repopulate.
    private string Get4ByteHexDid(byte did)
    {
        var bytes = Model.GetIdentifier(did);
        return bytes is { Length: 4 } ? "0x" + Convert.ToHexString(bytes) : "";
    }

    private bool Set4ByteHexDid(byte did, string? value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0) return Model.RemoveIdentifier(did);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != 8) return false;
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2),
                               System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out bytes[i]))
                return false;
        }
        var current = Model.GetIdentifier(did);
        if (current != null && current.AsSpan().SequenceEqual(bytes)) return false;
        Model.SetIdentifier(did, bytes);
        return true;
    }

    /// <summary>FC.BS byte sent on First Frame reception. Hex display, e.g. "0x01".</summary>
    public string FlowControlBlockSizeHex
    {
        get => $"0x{Model.FlowControlBlockSize:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.FlowControlBlockSize != v)
            {
                Model.FlowControlBlockSize = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>FC.STmin byte sent on First Frame reception. Hex display, e.g. "0x00".</summary>
    public string FlowControlSeparationTimeHex
    {
        get => $"0x{Model.FlowControlSeparationTime:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.FlowControlSeparationTime != v)
            {
                Model.FlowControlSeparationTime = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Number of bytes in the $36 startingAddress field. Spec range 2..4;
    /// the editor surfaces these as a fixed-options dropdown so a bad value
    /// can't sneak in. Default 4 matches T43-era ECUs.
    /// </summary>
    public int DownloadAddressByteCount
    {
        get => Model.DownloadAddressByteCount;
        set
        {
            if (value is < 2 or > 4) return;
            if (Model.DownloadAddressByteCount == value) return;
            Model.DownloadAddressByteCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The fixed list of valid values for the editor dropdown.</summary>
    public IReadOnlyList<int> DownloadAddressByteCountOptions { get; } = new[] { 2, 3, 4 };

    private static bool TryParseHexByte(string s, out byte v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set => SetField(ref selectedPid, value);
    }

    public void AddPid()
    {
        // Pick the next free address - start at 0x0001 and walk up. We stay
        // in the 16-bit space for the auto-pick so the new PID is reachable
        // by a wire-format $22 request without first going through $2D.
        uint addr = 0x0001;
        while (Model.GetPid(addr) != null) addr++;

        var pid = new Pid
        {
            Address = addr,
            Name = "New PID",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            Unit = "",
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 50, Offset = 50, FrequencyHz = 1.0 },
        };
        Model.AddPid(pid);
        var vm = new PidViewModel(pid, this);
        Pids.Add(vm);
        SelectedPid = vm;
    }

    public void RemoveSelectedPid()
    {
        if (selectedPid == null) return;
        Model.RemovePid(selectedPid.Model);
        Pids.Remove(selectedPid);
        SelectedPid = null;
    }

    public void RaisePidsChanged() => Model.RaisePidsChanged();

    private static bool TryParseHexU16(string s, out ushort v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    // ---------------- Security ($27) ----------------

    private const string NoneSecurityModuleLabel = "(none)";

    /// <summary>All registered module IDs, prefixed with a synthetic "(none)" entry.</summary>
    public ObservableCollection<string> AvailableSecurityModuleIds { get; }

    /// <summary>Editable key→string map for the module's SecurityModuleConfig JsonElement.</summary>
    public ObservableCollection<KeyValueEntry> SecurityModuleConfigEntries { get; }

    // ---- Live security state (refreshed from MainWindow refresh timer) ----

    private string securityStatusText = "Locked";
    public string SecurityStatusText
    {
        get => securityStatusText;
        private set => SetField(ref securityStatusText, value);
    }

    private string securityFailedAttemptsText = "0 / 3";
    public string SecurityFailedAttemptsText
    {
        get => securityFailedAttemptsText;
        private set => SetField(ref securityFailedAttemptsText, value);
    }

    private string securityPendingSeedText = "(none)";
    public string SecurityPendingSeedText
    {
        get => securityPendingSeedText;
        private set => SetField(ref securityPendingSeedText, value);
    }

    /// <summary>
    /// Simulated power-cycle for this ECU. Combines the spec-defined $20
    /// ReturnToNormalMode exit (clears programming/download/$28/$A5 state and
    /// emits the unsolicited $60 if a host is still attached) with a full
    /// security re-lock. This is the canonical behaviour for any "Reset ECU
    /// state" button across the workspace tabs - keep all such buttons routed
    /// through here so they stay in sync.
    ///
    /// Note: $20 alone is spec-correct to NOT touch security (GMW3110 §8.5.6.2),
    /// so EcuExitLogic deliberately omits it; the power-cycle delta is added here.
    /// </summary>
    public void ResetEcuState(DpidScheduler scheduler)
    {
        var channel = Model.State.LastEnhancedChannel;
        EcuExitLogic.Run(Model, scheduler, channel);
        ResetSecurityState();
    }

    /// <summary>
    /// Re-locks the ECU and clears every transient $27 field on its NodeState
    /// (unlocked level, pending seed, failed-attempt counter, lockout deadline,
    /// module-private bookkeeping). Equivalent to a power-cycle for this one
    /// ECU's security subsystem. Bound to the "Reset state" button in the
    /// Security tab.
    /// </summary>
    public void ResetSecurityState()
    {
        var s = Model.State;
        lock (s.Sync)
        {
            s.SecurityUnlockedLevel = 0;
            s.SecurityPendingSeedLevel = 0;
            s.SecurityLastIssuedSeed = null;
            s.SecurityFailedAttempts = 0;
            s.SecurityLockoutUntilMs = 0;
            s.SecurityModuleState = null;
        }
        // The 10Hz refresh tick would catch this within 100ms; push an
        // immediate update so the click feels instant.
        RefreshSecurity(0);
    }

    /// <summary>Called from the main refresh timer to update the live security display.</summary>
    public void RefreshSecurity(long nowMs)
    {
        var s = Model.State;
        if (s.IsInLockout(nowMs))
        {
            double remainingSec = (s.SecurityLockoutUntilMs - nowMs) / 1000.0;
            SecurityStatusText = $"Locked out - {remainingSec:F1} s remaining";
        }
        else if (s.SecurityUnlockedLevel > 0)
        {
            SecurityStatusText = $"Unlocked (level {s.SecurityUnlockedLevel})";
        }
        else
        {
            SecurityStatusText = "Locked";
        }

        SecurityFailedAttemptsText = $"{s.SecurityFailedAttempts} / 3";

        var seed = s.SecurityLastIssuedSeed;
        if (s.SecurityPendingSeedLevel == 0 || seed is null)
        {
            SecurityPendingSeedText = "(none)";
        }
        else
        {
            SecurityPendingSeedText =
                $"level {s.SecurityPendingSeedLevel}, seed = {string.Join(" ", seed.Select(b => b.ToString("X2")))}";
        }
    }

    /// <summary>
    /// When true the security module short-circuits every $27 step to a
    /// positive response without invoking the algorithm. Persisted per ECU.
    /// </summary>
    public bool BypassSecurity
    {
        get => Model.BypassSecurity;
        set { if (Model.BypassSecurity != value) { Model.BypassSecurity = value; OnPropertyChanged(); } }
    }

    private string selectedSecurityModuleId;
    public string SelectedSecurityModuleId
    {
        get => selectedSecurityModuleId;
        set
        {
            if (selectedSecurityModuleId == value) return;
            selectedSecurityModuleId = value;
            OnPropertyChanged();
            ApplyModuleSelection();
        }
    }

    private void ApplyModuleSelection()
    {
        if (selectedSecurityModuleId == NoneSecurityModuleLabel)
        {
            Model.SecurityModule = null;
        }
        else
        {
            Model.SecurityModule = SecurityModuleRegistry.Create(selectedSecurityModuleId);
            Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
        }
    }

    private void OnSecurityEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (KeyValueEntry old in e.OldItems) old.PropertyChanged -= OnSecurityEntryPropertyChanged;
        if (e.NewItems != null)
            foreach (KeyValueEntry n in e.NewItems) n.PropertyChanged += OnSecurityEntryPropertyChanged;
        PushEntriesToModel();
    }

    private void OnSecurityEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PushEntriesToModel();

    private void PushEntriesToModel()
    {
        Model.SecurityModuleConfig = BuildJsonFromEntries();
        Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
    }

    private void LoadEntriesFromJson(JsonElement? json)
    {
        SecurityModuleConfigEntries?.Clear();
        if (json is null || json.Value.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in json.Value.EnumerateObject())
            SecurityModuleConfigEntries!.Add(new KeyValueEntry(prop.Name, ValueToDisplayString(prop.Value)));
    }

    private JsonElement? BuildJsonFromEntries()
    {
        // Every entry value persists as a JSON string - modules parse strings
        // themselves (hex bytes, integers, etc.). Round-tripping a number-typed
        // load lands as a stringified number; modules that care can parse it.
        var dict = new Dictionary<string, string>();
        foreach (var e in SecurityModuleConfigEntries)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            dict[e.Key] = e.Value ?? "";
        }
        if (dict.Count == 0) return null;
        return JsonSerializer.SerializeToElement(dict);
    }

    private static string ValueToDisplayString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.GetRawText(),
    };
}

public sealed class KeyValueEntry : NotifyPropertyChangedBase
{
    private string key = "";
    private string value = "";

    // Parameterless ctor lets the DataGrid create new rows when CanUserAddRows=True.
    public KeyValueEntry() { }
    public KeyValueEntry(string key, string value) { this.key = key; this.value = value; }

    public string Key
    {
        get => key;
        set => SetField(ref key, value);
    }

    public string Value
    {
        get => value;
        set => SetField(ref this.value, value);
    }
}

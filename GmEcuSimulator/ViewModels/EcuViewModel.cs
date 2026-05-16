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
        AutoPopulateDidsCommand = new RelayCommand(AutoPopulateMissingDids);

        // Identifiers grid: every well-known $1A DID gets a pre-populated row
        // so the user can fill in values without "add" clicks. Leave Value
        // blank to keep a DID unconfigured ($1A returns NRC $31 for it). Any
        // non-standard DIDs already on the model (e.g. from a loaded bin or
        // a hand-edited JSON) are appended after the well-known set so nothing
        // is hidden. We DON'T subscribe to Model.IdentifiersChanged - the
        // existing well-known-DID textboxes ($90/$92/$98/$C1/$C2/$CC) call
        // SetIdentifier on every keystroke and re-running RebuildGrid would
        // tear the user's open DataGrid edit out from under them. External
        // mutations (LoadInfoFromBin) call RefreshIdentifiersGrid explicitly.
        Identifiers = new ObservableCollection<IdentifierRowViewModel>();
        RebuildIdentifierRows();

        // Pre-grid identity-field validators dropped with the dedicated
        // textboxes. The Identifiers grid does its own value-format handling
        // (ASCII vs hex toggle) and doesn't surface a red-border indicator.
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

    // ---------------- $1A ECU identity DIDs ----------------
    //
    // Editing happens through the Identifiers grid further down the ECU
    // panel - every well-known DID gets a pre-populated row. The grid pushes
    // each value straight to EcuNode.SetIdentifier, which is the same storage
    // the $1A handler reads from at runtime. Persisted by the existing
    // IdentifierDto round-trip in ConfigStore.
    //
    // "Load Info From Bin..." (LoadInfoFromBinCommand below) writes extracted
    // values directly into EcuNode.Identifiers and then refreshes the grid.

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

        // Ask the user which load mode to use BEFORE touching the file - if
        // they cancel, no work is done. Yes = replace-all (destructive but
        // explicit), No = merge (keeps user-edited / auto-populated values),
        // Cancel = bail.
        var modeChoice = MessageBox.Show(
            "Replace all existing $1A DIDs on this ECU with what the bin contains?\n\n" +
            "[Yes] Replace all - clear every DID on this ECU first, then write only " +
            "what the bin surfaces. DIDs the bin can't extract end up unconfigured.\n\n" +
            "[No] Add only if blank - keep existing DIDs; only fill ones currently empty. " +
            "User edits and prior auto-populate values are preserved.\n\n" +
            "[Cancel] Don't load.",
            "Load Info From Bin",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (modeChoice == MessageBoxResult.Cancel) return;
        var mode = modeChoice == MessageBoxResult.Yes
            ? BinIdentificationApplier.LoadMode.ReplaceAll
            : BinIdentificationApplier.LoadMode.Merge;

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

        // Delegate to the testable Core helper. It clears + writes the model
        // per the chosen mode and returns the lists for the summary dialog.
        var outcome = BinIdentificationApplier.Apply(Model, result, mode);
        var applied = outcome.Applied.ToList();
        var skipped = outcome.Skipped.ToList();
        if (!string.IsNullOrEmpty(result.CalibrationPartNumber))
            applied.Add($"Cal P/N ({result.CalibrationPartNumber} - not stored, no fixed DID)");

        // Compose a summary message - shows which fields were populated and
        // surfaces any parser warnings (e.g. "no trampoline pattern detected").
        string modeLabel = mode == BinIdentificationApplier.LoadMode.ReplaceAll
            ? "Replace all (existing DIDs cleared first)"
            : "Add only if blank (existing DIDs preserved)";
        var lines = new List<string>
        {
            $"Mode: {modeLabel}",
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
        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"Kept existing (precedence: user/auto-populate wins): {string.Join(", ", skipped)}");
        }
        if (result.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings:");
            foreach (var w in result.Warnings) lines.Add("  - " + w);
        }

        // Sync the Identifiers grid with the model since we just wrote five
        // well-known DIDs through their dedicated property setters.
        RefreshIdentifiersGrid();

        MessageBox.Show(string.Join(Environment.NewLine, lines),
            "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// True iff the model has a non-empty byte array stored for this DID.
    /// Used by the precedence guard in <see cref="AutoPopulateMissingDids"/>
    /// so the auto-populate pass doesn't clobber a higher-precedence value
    /// (user hand-edit and bin-load both take precedence over defaults).
    /// </summary>
    private bool HasIdentifier(byte did)
    {
        var bytes = Model.GetIdentifier(did);
        return bytes != null && bytes.Length > 0;
    }

    /// <summary>
    /// "Auto-populate missing $1A DIDs" command. Prompts the user for one of
    /// three modes, then fills well-known DIDs (<see cref="Gmw3110DidNames.KnownDids"/>)
    /// with placeholder values from <see cref="DefaultDidValues"/>. DIDs that
    /// already have a non-empty value are NEVER overwritten - they stay even
    /// in the aggressive mode. The two modes differ in how they treat
    /// sticky-user blanks (source=User with no bytes - the user explicitly
    /// cleared a row).
    /// </summary>
    public RelayCommand AutoPopulateDidsCommand { get; }

    private void AutoPopulateMissingDids()
    {
        // Three-way prompt: Yes = aggressive (fill all blanks including ones
        // the user deliberately cleared), No = conservative (default - keep
        // sticky-user blanks), Cancel = bail with no model change.
        var choice = MessageBox.Show(
            "How should Auto-populate handle DIDs the user previously cleared?\n\n" +
            "[Yes] Overwrite user-blank fields - fill every well-known DID that is " +
            "currently empty, including ones the user explicitly cleared (the sticky-User " +
            "rule is ignored for this run; rows already containing a value still aren't " +
            "touched).\n\n" +
            "[No] Populate blanks - only fill DIDs that are blank AND not tagged " +
            "source=user. Rows the user deliberately cleared stay empty.\n\n" +
            "[Cancel] Don't change anything.",
            "Auto-populate DIDs",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;
        bool overwriteUserBlanks = choice == MessageBoxResult.Yes;

        int populated = 0;
        int respectedUserBlanks = 0;
        foreach (var did in Common.Protocol.Gmw3110DidNames.KnownDids)
        {
            // Existing values are never overwritten by Auto-populate - the
            // user owns rows with content regardless of mode.
            if (HasIdentifier(did)) continue;

            // Sticky-User blanks: skip in conservative mode; fill in aggressive.
            // Auto / Bin / Blank sources always get the default.
            if (!overwriteUserBlanks &&
                Model.GetIdentifierSource(did) == Common.Protocol.DidSource.User)
            {
                respectedUserBlanks++;
                continue;
            }

            var bytes = Common.Protocol.DefaultDidValues.Get(did);
            if (bytes == null || bytes.Length == 0) continue;
            Model.SetIdentifier(did, bytes, Common.Protocol.DidSource.Auto);
            populated++;
        }
        RefreshIdentifiersGrid();

        if (populated == 0)
        {
            var msg = respectedUserBlanks > 0
                ? $"Nothing to do - every well-known DID already has a value or " +
                  $"is a user-blanked row. {respectedUserBlanks} row(s) tagged " +
                  $"source=user were preserved; re-run and pick 'Overwrite user-blank " +
                  $"fields' to fill those too."
                : "Nothing to do - every well-known DID already has a value. " +
                  "Clear an entry in the Identifiers grid (Value column) and re-run " +
                  "to re-fill it with the placeholder default.";
            MessageBox.Show(msg, "Auto-populate DIDs",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
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

    /// <summary>
    /// GMW3110 §8.16 SPS classification. Default A is a normal running ECU;
    /// C activates the blank-ECU state machine that GM SPS / DPS uses during
    /// programming-discovery (silent until $A2 received while $28 active,
    /// then responds on SPS_PrimeRsp). The editor exposes this as a dropdown
    /// driven by <see cref="SpsTypeOptions"/>.
    /// </summary>
    public Common.Protocol.SpsType SpsType
    {
        get => Model.SpsType;
        set
        {
            if (Model.SpsType == value) return;
            Model.SpsType = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The fixed list of valid SpsType values for the editor dropdown.</summary>
    public IReadOnlyList<Common.Protocol.SpsType> SpsTypeOptions { get; } = new[]
    {
        Common.Protocol.SpsType.A,
        Common.Protocol.SpsType.B,
        Common.Protocol.SpsType.C,
    };

    /// <summary>
    /// 8-bit diagnostic address used to derive SPS_PrimeReq/Rsp for SpsType.C.
    /// For type A/B this field is informational. Hex display, e.g. "0x11".
    /// Setting this does NOT auto-update the CAN ID fields - the user is
    /// expected to maintain the relationship PhysReq = $000|addr,
    /// USDT resp = $300|addr when configuring a SPS_TYPE_C ECU.
    /// </summary>
    public string DiagnosticAddressHex
    {
        get => $"0x{Model.DiagnosticAddress:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.DiagnosticAddress != v)
            {
                Model.DiagnosticAddress = v;
                OnPropertyChanged();
            }
        }
    }

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

    // ---------------- Identifiers grid ----------------
    //
    // Generic table covering every $1A DID configured on this ECU. The five
    // well-known DID textboxes higher up the panel ($90/$92/$98/$C1/$C2/$CC)
    // are still there for convenience, but the grid is the canonical surface
    // for anything DPS / a real tester might read - VINs, part numbers, calib
    // IDs, supplier metadata, programming date, manufacturer enable counters,
    // and the long $C0..$CA / $F1..$F4 ranges that the DPS Get Controller
    // Info flow walks through. Persists through ConfigStore.EcuDtoFrom's
    // existing Identifiers round-trip - no schema change needed.

    /// <summary>Fixed rows: every well-known $1A DID plus any extras already
    /// configured on the model. Empty Value means the DID isn't set (and $1A
    /// will return NRC $31 for it).</summary>
    public ObservableCollection<IdentifierRowViewModel> Identifiers { get; }

    /// <summary>
    /// Builds the row set: one row per <see cref="Gmw3110DidNames.KnownDids"/>
    /// entry, plus any extras already present on <see cref="EcuNode.Identifiers"/>
    /// that aren't in the well-known list (e.g. loaded from a custom JSON).
    /// Existing rows are replaced; safe to call when the model changes
    /// externally (LoadInfoFromBin, project file open).
    /// </summary>
    private void RebuildIdentifierRows()
    {
        Identifiers.Clear();
        var known = new HashSet<byte>(Gmw3110DidNames.KnownDids);
        foreach (var did in Gmw3110DidNames.KnownDids)
        {
            var bytes = Model.GetIdentifier(did) ?? Array.Empty<byte>();
            Identifiers.Add(new IdentifierRowViewModel(Model, did, bytes));
        }
        // Append any extras the model already has (e.g. from a loaded bin or
        // hand-edited JSON), sorted by DID for stable ordering.
        foreach (var kv in Model.Identifiers.OrderBy(kv => kv.Key))
        {
            if (known.Contains(kv.Key)) continue;
            Identifiers.Add(new IdentifierRowViewModel(Model, kv.Key, kv.Value));
        }
    }

    /// <summary>
    /// Rebuilds the grid rows after an external mutation (e.g. Load Info From
    /// Bin populates several DIDs through their dedicated property setters).
    /// Public so callers outside the VM (e.g. project-file load) can refresh
    /// the displayed rows.
    /// </summary>
    public void RefreshIdentifiersGrid() => RebuildIdentifierRows();
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

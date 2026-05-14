using System.Windows.Media;
using Core.Scheduler;

namespace GmEcuSimulator.ViewModels;

// Live monitor for the GMW3110 programming-flow state of one ECU. Each
// programming step exposes a traffic-light brush (red = precondition not
// met, amber = the next step the host should perform, green = satisfied).
// The truth lives on NodeState; we poll it from MainWindow's existing
// 100 ms refresh timer so we don't have to push INotifyPropertyChanged
// through the bus.
//
// Wired to whichever ECU is selected in the sidebar - SelectedEcu on
// MainViewModel pushes its value into Ecu via a setter hook.
public sealed class DownloadWorkspaceViewModel : NotifyPropertyChangedBase
{
    // Traffic-light colours are intentionally NOT theme-bound. The metaphor is
    // universal; remapping the lights for a dark palette would defeat the
    // purpose. Frozen so they're cheap to share across all 7 ellipses.
    private static readonly SolidColorBrush RedBrush   = MakeFrozenBrush(0xC0, 0x39, 0x2B);
    private static readonly SolidColorBrush AmberBrush = MakeFrozenBrush(0xF4, 0xB1, 0x00);
    private static readonly SolidColorBrush GreenBrush = MakeFrozenBrush(0x2E, 0xCC, 0x71);
    private static readonly SolidColorBrush DimBrush   = MakeFrozenBrush(0x33, 0x33, 0x33);

    private static SolidColorBrush MakeFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly DpidScheduler scheduler;
    private EcuViewModel? ecu;

    public RelayCommand ResetEcuStateCommand { get; }

    public DownloadWorkspaceViewModel(DpidScheduler scheduler)
    {
        this.scheduler = scheduler;
        ResetEcuStateCommand = new RelayCommand(ResetEcuState, () => ecu != null);
    }

    public EcuViewModel? Ecu
    {
        get => ecu;
        set
        {
            if (SetField(ref ecu, value))
            {
                OnPropertyChanged(nameof(EcuName));
                Refresh();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string EcuName => ecu?.Name ?? "(no ECU selected)";

    // One brush per step. Bound to the corresponding Ellipse.Fill in XAML.
    private Brush b28      = DimBrush; public Brush Brush28      { get => b28;      private set => SetField(ref b28,      value); }
    private Brush bA5Req   = DimBrush; public Brush BrushA5Req   { get => bA5Req;   private set => SetField(ref bA5Req,   value); }
    private Brush bA5En    = DimBrush; public Brush BrushA5Enable{ get => bA5En;    private set => SetField(ref bA5En,    value); }
    private Brush b27      = DimBrush; public Brush Brush27      { get => b27;      private set => SetField(ref b27,      value); }
    private Brush b34      = DimBrush; public Brush Brush34      { get => b34;      private set => SetField(ref b34,      value); }
    private Brush b36      = DimBrush; public Brush Brush36      { get => b36;      private set => SetField(ref b36,      value); }
    private Brush b20      = DimBrush; public Brush Brush20      { get => b20;      private set => SetField(ref b20,      value); }

    private string s36Progress = "0 / 0 bytes";
    public string S36Progress { get => s36Progress; private set => SetField(ref s36Progress, value); }

    private string statusLine = "Idle - no programming session active";
    public string StatusLine { get => statusLine; private set => SetField(ref statusLine, value); }

    /// <summary>
    /// Re-reads the selected ECU's NodeState and updates every brush + caption.
    /// Called by MainWindow's 100 ms refresh timer; safe to call repeatedly with
    /// no change (SetField suppresses no-op PropertyChanged events).
    /// </summary>
    public void Refresh()
    {
        var st = ecu?.Model.State;
        if (st == null)
        {
            Brush28 = BrushA5Req = BrushA5Enable = Brush27 = Brush34 = Brush36 = Brush20 = DimBrush;
            S36Progress = "0 / 0 bytes";
            StatusLine = "(no ECU selected)";
            return;
        }

        bool g28    = st.NormalCommunicationDisabled;
        bool gA5Req = st.ProgrammingModeRequested;
        bool gA5En  = st.ProgrammingModeActive;
        bool g27    = st.SecurityUnlockedLevel > 0;
        bool g34    = st.DownloadActive;
        bool g36    = st.DownloadActive
                      && st.DownloadDeclaredSize > 0
                      && st.DownloadBytesReceived >= st.DownloadDeclaredSize;

        // Each step is green if its own precondition is met; amber if the prior
        // step is green but this one isn't; red otherwise. The chain mirrors the
        // ordering required by GMW3110 §8.12 (RequestDownload preconditions).
        Brush28       = Light(g28,    prevDone: true);
        BrushA5Req    = Light(gA5Req, prevDone: g28);
        BrushA5Enable = Light(gA5En,  prevDone: gA5Req);
        Brush27       = Light(g27,    prevDone: gA5En);
        Brush34       = Light(g34,    prevDone: g27);
        Brush36       = Light(g36,    prevDone: g34);
        // $20 ReturnToNormalMode lights amber whenever there's anything to exit
        // FROM (programming mode active or normal-comm disabled). Goes back to
        // dim once everything's reset.
        Brush20       = (gA5En || g28) ? AmberBrush : DimBrush;

        S36Progress = st.DownloadDeclaredSize == 0
            ? "0 / 0 bytes"
            : $"{st.DownloadBytesReceived:N0} / {st.DownloadDeclaredSize:N0} bytes";

        StatusLine = (g28, gA5En, g27, g34, g36) switch
        {
            (false, _, _, _, _)               => "Waiting for $28 DisableNormalCommunication",
            (true, false, _, _, _)            => "Waiting for $A5 ProgrammingMode",
            (true, true, false, _, _)         => "Waiting for $27 SecurityAccess unlock",
            (true, true, true, false, _)      => "Unlocked - waiting for $34 RequestDownload",
            (true, true, true, true, false)   => "Receiving $36 TransferData payload",
            (true, true, true, true, true)    => "Download complete - send $20 to exit",
        };
    }

    private static Brush Light(bool done, bool prevDone) =>
        done ? GreenBrush : (prevDone ? AmberBrush : RedBrush);

    private void ResetEcuState()
    {
        if (ecu == null) return;
        // Routed through EcuViewModel so every "Reset ECU state" button in the
        // workspace tabs ends up doing exactly the same thing (power-cycle =
        // $20 exit + full security re-lock).
        ecu.ResetEcuState(scheduler);
        Refresh();
    }
}

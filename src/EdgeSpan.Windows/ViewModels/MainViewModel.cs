using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using EdgeSpan.Input;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Session;
using EdgeSpan.Windows.Services;

namespace EdgeSpan.Windows.ViewModels;

public sealed class DeviceRow : INotifyPropertyChanged
{
    public DeviceRow(string id) => Id = id;

    public string Id { get; }
    public string Name { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string Badge { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(DeviceSnapshot snap, string lang, string path)
    {
        Name = string.IsNullOrWhiteSpace(snap.Name) ? snap.Id : snap.Name;
        Detail = $"{snap.Address}:{snap.Port}  ·  {snap.PcEdge} → {snap.ReturnEdge}";
        Badge = snap.Active ? Catalog.Get(lang, "hasControl") : path;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
    }
}

public sealed class PeerRow
{
    public PeerRow(DiscoveredPeer peer)
    {
        Id = peer.Id;
        Name = peer.Name;
        Address = peer.Address;
        Port = peer.Port;
        Detail = $"{peer.Address}:{peer.Port}  ·  {peer.ScreenW}×{peer.ScreenH}";
    }

    public string Id { get; }
    public string Name { get; }
    public string Address { get; }
    public int Port { get; }
    public string Detail { get; }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SessionRuntime _runtime;
    private DeviceRow? _selected;
    private string _loadedId = "";
    private string _qrText = "";
    private int _page;
    private bool _suppressClipboard;

    public MainViewModel(SessionRuntime runtime)
    {
        _runtime = runtime;
        _boundPort = runtime.Host.Settings.ListenPort;
        RestartCommand = new RelayCommand(RestartApp);
        PickGlowColorCommand = new RelayCommand<string>(hex => { if (!string.IsNullOrEmpty(hex)) EdgeGlowColor = hex; });
        _autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _autosave.Tick += (_, _) => AutosaveTick();
        _autosave.Start();
        ShowConnectCommand = new RelayCommand(() => Page = 7);
        ShowOverviewCommand = new RelayCommand(() => Page = 0);
        ShowDevicesCommand = new RelayCommand(() => Page = 1);
        ShowLayoutCommand = new RelayCommand(() => Page = 2);
        ShowNetworkCommand = new RelayCommand(() => Page = 3);
        ShowPairingCommand = new RelayCommand(() => Page = 4);
        ShowLicenseCommand = new RelayCommand(() => Page = 5);
        ShowAboutCommand = new RelayCommand(() => Page = 6);
        ShowHomeCommand = new RelayCommand(() => Page = 0);
        ShowArrangeCommand = new RelayCommand(() => Page = 2);
        ShowTransfersCommand = new RelayCommand(() => Page = 8);
        ShowSettingsCommand = new RelayCommand(() => Page = 3);
        SystemThemeCommand = new RelayCommand(() => SetThemeMode("system"));
        EnglishCommand = new RelayCommand(() => SetLanguage("en"));
        PersianCommand = new RelayCommand(() => SetLanguage("fa"));
        DarkCommand = new RelayCommand(() => SetTheme(true));
        LightCommand = new RelayCommand(() => SetTheme(false));
        IsDark = !string.Equals(_runtime.Host.Settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
        Ui = new UiText(_runtime.Host.Settings.Language);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        SaveDeviceCommand = new RelayCommand(SaveDevice);
        RemoveDeviceCommand = new RelayCommand(RemoveSelected);
        AddManualCommand = new RelayCommand(AddManual);
        AddPeerCommand = new RelayCommand<PeerRow>(AddPeer);
        StartPairingCommand = new RelayCommand(StartPairing);
        ChooseUsbCommand = new RelayCommand(() => { _runtime.BeginUsb(); Pull(); });
        ChooseLanCommand = new RelayCommand(() => { _runtime.BeginLan(); Pull(); });
        ChooseRelayCommand = new RelayCommand(ChooseRelay);
        RetryConnectCommand = new RelayCommand(RetryConnect);
        SwitchMethodCommand = new RelayCommand(() => { _runtime.StopLink(); Pull(); });
        CopyPinCommand = new RelayCommand(CopyPin);
        ActivateCommand = new RelayCommand(Activate);
        LockCommand = new RelayCommand(() => { _runtime.Host.ToggleLock(); Pull(); });
        CycleCommand = new RelayCommand(() => { _runtime.Host.Cycle(); Pull(); });
        RecordHotkeyCommand = new RelayCommand(BeginHotkeyCapture);
        OpenSamsungCommand = new RelayCommand(OpenSamsung);
        ExportLogCommand = new RelayCommand(ExportLog);
        SimulateLeaveCommand = new RelayCommand(SimulateLeave);
        DetectCommand = new RelayCommand(DetectMonitors);
        IdentifyCommand = new RelayCommand(() => IdentifyRequested?.Invoke());
        SimulateReturnCommand = new RelayCommand(SimulateReturn);
        ApplyLanguage();
        Page = 7;
        Pull();
        ApplyShotOverrides();
        runtime.Effect += effect =>
        {
            if (effect is Effect.ClipboardArrived arrived)
                RemoteClipboard?.Invoke(arrived.Text);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;
    public event Action? ThemeChanged;
    public event Action? ChromeChanged;
    public event Action<string>? CopyRequested;
    public event Action<string>? RemoteClipboard;

    public HostController Host => _runtime.Host;
    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<PeerRow> Peers { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public RelayCommand ShowConnectCommand { get; }
    public RelayCommand ShowHomeCommand { get; }
    public RelayCommand ShowArrangeCommand { get; }
    public RelayCommand ShowTransfersCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand SystemThemeCommand { get; }

    /// <summary>Win11-style UI strings, bound as {Binding Ui[key]}.</summary>
    public UiText Ui { get; private set; } = new("en");

    public bool FollowSystemTheme => string.Equals(_runtime.Host.Settings.Theme, "system", StringComparison.OrdinalIgnoreCase);

    public int ThemeIndex
    {
        get => FollowSystemTheme ? 0 : IsDark ? 2 : 1;
        set => SetThemeMode(value switch { 1 => "light", 2 => "dark", _ => "system" });
    }

    public int LanguageIndex
    {
        get => Catalog.IsRtl(_runtime.Host.Settings.Language) ? 1 : 0;
        set => SetLanguage(value == 1 ? "fa" : "en");
    }

    private string _search = "";

    /// <summary>"Find a setting": jumps to the page whose keywords match.</summary>
    public string SearchText
    {
        get => _search;
        set
        {
            _search = value ?? "";
            Notify(nameof(SearchText));
            var page = UiText.FindPage(_search);
            if (page is { } target && target != _page)
                Page = target;
        }
    }

    public bool ShowHome => _page is 0 or 7;
    public bool ShowArrange => _page == 2;
    public bool ShowDevicesPage => _page is 1 or 4;
    public bool ShowTransfers => _page == 8;
    public bool ShowSettingsPage => _page is 3 or 5 or 6;
    public bool HomeSelected => ShowHome;
    public bool ArrangeSelected => ShowArrange;
    public bool DevicesNavSelected => ShowDevicesPage;
    public bool TransfersSelected => ShowTransfers;
    public bool SettingsSelected => ShowSettingsPage;
    public string PageTitle => _page switch
    {
        0 or 7 => Ui["navHome"],
        2 => Ui["navArrange"],
        1 or 4 => Ui["navDevicesW"],
        8 => Ui["navTransfers"],
        _ => Ui["navSettings"]
    };
    public RelayCommand ShowOverviewCommand { get; }
    public RelayCommand ShowDevicesCommand { get; }
    public RelayCommand ShowLayoutCommand { get; }
    public RelayCommand ShowNetworkCommand { get; }
    public RelayCommand ShowPairingCommand { get; }
    public RelayCommand ShowLicenseCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand EnglishCommand { get; }
    public RelayCommand PersianCommand { get; }
    public RelayCommand DarkCommand { get; }
    public RelayCommand LightCommand { get; }
    public bool IsDark { get; private set; } = true;
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand SaveDeviceCommand { get; }
    public RelayCommand RemoveDeviceCommand { get; }
    public RelayCommand AddManualCommand { get; }
    public RelayCommand<PeerRow> AddPeerCommand { get; }
    public RelayCommand StartPairingCommand { get; }
    public RelayCommand ChooseUsbCommand { get; }
    public RelayCommand ChooseLanCommand { get; }
    public RelayCommand ChooseRelayCommand { get; }
    public RelayCommand RetryConnectCommand { get; }
    public RelayCommand SwitchMethodCommand { get; }
    public RelayCommand CopyPinCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand CycleCommand { get; }
    public RelayCommand RecordHotkeyCommand { get; }
    public RelayCommand OpenSamsungCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand SimulateLeaveCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand RestartCommand { get; }

    // Windows 11-style auto-save: every form change persists on its own (no Save button). Toggles/combos
    // are written on the next tick; text and number fields once they have been stable for 500 ms.
    private readonly Avalonia.Threading.DispatcherTimer _autosave;
    private readonly int _boundPort;
    private string _settingsSig = "";
    private string _deviceSig = "";
    private string _pendingSettings = "";
    private string _pendingDevice = "";
    private long _settingsChangedAt;
    private long _deviceChangedAt;
    private long _savedAt;
    public bool RestartRequired { get; private set; }
    public bool ShowSaved => _savedAt > 0 && Environment.TickCount64 - _savedAt < 1800;

    private string SettingsSig() => string.Join("\u001f", DisplayName, ListenPort, ClipboardShare, FileTransfer, LaunchAtStartup,
        HotkeySwitch, HotkeyLock, HotkeySwitchEnabled, DeviceNumberHotkeys, EdgeGlowEnabled, EdgeGlowColor, EdgeGlowIntensity,
        EdgeGlowThickness, BreakthroughEnabled, BreakthroughIntensity, ReduceMotion, SoundEnabled, InternetEnabled, HandoffPc,
        RelayUrl, StunHost, StunPort, PublicHost, PublicPort);

    private string DeviceSig() => _selected == null ? "" : string.Join("\u001f", _selected.Id, EditName, EditAddress, EditPort,
        EditWidth, EditHeight, PcEdgeIndex, ReturnEdgeIndex, AnchorIndex, CornerIndex, EditOffset, EditSpan, EditCornerSize, EditSpeed);

    private string SettingsFlags() => string.Join(",", ClipboardShare, FileTransfer, LaunchAtStartup, HotkeySwitchEnabled,
        DeviceNumberHotkeys, EdgeGlowEnabled, BreakthroughEnabled, ReduceMotion, SoundEnabled, InternetEnabled, HandoffPc);

    private string DeviceFlags() => string.Join(",", _selected?.Id, PcEdgeIndex, ReturnEdgeIndex, AnchorIndex, CornerIndex);

    private string _flagsSig = "";
    private string _deviceFlagsSig = "";

    private void ResetAutosave()
    {
        _settingsSig = _pendingSettings = SettingsSig();
        _deviceSig = _pendingDevice = DeviceSig();
        _flagsSig = SettingsFlags();
        _deviceFlagsSig = DeviceFlags();
    }

    private void AutosaveTick()
    {
        var now = Environment.TickCount64;
        if (_settingsSig.Length == 0)
        {
            ResetAutosave();
            return;
        }
        var s = SettingsSig();
        if (s != _pendingSettings)
        {
            // Bool/combos (same text length) save at once; typed fields wait for 500 ms of quiet.
            _settingsChangedAt = SettingsFlags() != _flagsSig ? now - 500 : now;
            _pendingSettings = s;
        }
        if (s != _settingsSig && now - _settingsChangedAt >= 500)
        {
            SaveSettings();
            _settingsSig = _pendingSettings = SettingsSig();
            _flagsSig = SettingsFlags();
            MarkSaved();
        }
        var d = DeviceSig();
        if (d != _pendingDevice)
        {
            _deviceChangedAt = DeviceFlags() != _deviceFlagsSig ? now - 500 : now;
            _pendingDevice = d;
        }
        if (d != _deviceSig && now - _deviceChangedAt >= 500)
        {
            if (_selected != null && _deviceSig.StartsWith(_selected.Id, StringComparison.Ordinal))
            {
                SaveDevice();
                MarkSaved();
            }
            _deviceSig = _pendingDevice = DeviceSig();
            _deviceFlagsSig = DeviceFlags();
        }
        if (_savedAt > 0 && now - _savedAt >= 1800)
        {
            _savedAt = 0;
            Notify(nameof(ShowSaved));
        }
    }

    private void MarkSaved()
    {
        _savedAt = Environment.TickCount64;
        // The UDP/TCP listeners bind once at start; a new port needs a restart (everything else applies live).
        RestartRequired = _runtime.Host.Settings.ListenPort != _boundPort;
        Notify(nameof(ShowSaved));
        Notify(nameof(RestartRequired));
    }

    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        Environment.Exit(0);
    }
    public RelayCommand IdentifyCommand { get; }
    public event Action? IdentifyRequested;

    /// <summary>Windows monitors at their real virtual-desktop positions (Arrange map).</summary>
    public IReadOnlyList<EdgeSpan.Layout.HostMonitor> Monitors { get; private set; } = Array.Empty<EdgeSpan.Layout.HostMonitor>();
    public string? SelectedDeviceId => _selected?.Id;
    public string ArrangeNote { get; private set; } = "";
    public bool HasArrangeNote => ArrangeNote.Length > 0;

    private void DetectMonitors()
    {
        if (OperatingSystem.IsWindows())
            _runtime.Host.SetHostMonitors(EdgeSpan.Windows.Services.WindowsInputCapture.EnumerateMonitors());
        Pull();
    }

    public void SelectDeviceById(string id)
    {
        var row = Devices.FirstOrDefault(d => d.Id == id);
        if (row != null)
            SelectedDevice = row;
    }
    public RelayCommand SimulateReturnCommand { get; }

    public string ProductName => ProductInfo.Name;
    public bool IsWindows => OperatingSystem.IsWindows();
    // Home merges the connect flow (old page 7) with the live status (old page 0).
    public bool ShowConnect => _page is 7 or 0;
    public bool ShowOverview => _page is 0 or 7;
    public bool ShowDevices => _page == 1;
    public bool ShowLayout => _page == 2;
    public bool ShowNetwork => _page == 3;
    public bool ShowPairing => _page == 4;
    public bool ShowLicense => _page == 5;
    public bool ShowAbout => _page == 6;
    public bool ConnectSelected => _page == 7;
    public bool OverviewSelected => _page == 0;
    public bool DevicesSelected => _page == 1;
    public bool LayoutSelected => _page == 2;
    public bool NetworkSelected => _page == 3;
    public bool PairingSelected => _page == 4;
    public bool LicenseSelected => _page == 5;
    public bool AboutSelected => _page == 6;

    public int Page
    {
        get => _page;
        private set
        {
            _page = value;
            Notify(nameof(Page));
            foreach (var name in new[]
            {
                nameof(ShowConnect), nameof(ShowOverview), nameof(ShowDevices), nameof(ShowLayout), nameof(ShowNetwork),
                nameof(ShowPairing), nameof(ShowLicense), nameof(ShowAbout),
                nameof(ConnectSelected), nameof(OverviewSelected), nameof(DevicesSelected), nameof(LayoutSelected),
                nameof(NetworkSelected), nameof(PairingSelected), nameof(LicenseSelected), nameof(AboutSelected),
                nameof(ShowHome), nameof(ShowArrange), nameof(ShowDevicesPage), nameof(ShowTransfers), nameof(ShowSettingsPage),
                nameof(HomeSelected), nameof(ArrangeSelected), nameof(DevicesNavSelected), nameof(TransfersSelected),
                nameof(SettingsSelected), nameof(PageTitle)
            })
                Notify(name);
        }
    }

    public DeviceRow? SelectedDevice
    {
        get => _selected;
        set
        {
            _selected = value;
            Notify();
            if (value?.Id != _loadedId)
                LoadEditor();
        }
    }

    public IReadOnlyList<DeviceSnapshot> LayoutDevices { get; private set; } = Array.Empty<DeviceSnapshot>();
    public string? ActiveDeviceId { get; private set; }
    public Bitmap? QrImage { get; private set; }

    public string StatusText { get; private set; } = "";
    public string ListenText { get; private set; } = "";
    public string ActiveText { get; private set; } = "";
    public string LockText { get; private set; } = "";
    public string RttText { get; private set; } = "";
    public string PacketText { get; private set; } = "";
    public string PathText { get; private set; } = "";
    public string LicenseText { get; private set; } = "";
    public string ProblemText { get; private set; } = "";
    public string PinText { get; private set; } = "";
    public string PairingState { get; private set; } = "";
    public string SavedText { get; private set; } = "";
    public bool NoDevices { get; private set; }
    public bool HasEditor { get; private set; }
    public bool PairingVisible { get; private set; }
    public bool HasProblems { get; private set; }
    public bool HasBindError { get; private set; }

    public string DisplayName { get; set; } = "";
    public decimal? ListenPort { get; set; } = ProductInfo.DefaultSessionPort;
    public bool ClipboardShare { get; set; } = true;

    /// <summary>Arrange segmented control; saved instantly (Windows 11 style) and applied to edge mapping live.</summary>
    public bool ScaleUniform
    {
        get => _runtime.Host.Settings.ArrangeScale == "uniform";
        set
        {
            if (value == ScaleUniform)
                return;
            _runtime.Host.SetArrangeScale(value ? "uniform" : "resolution");
            Notify(nameof(ScaleUniform));
            Notify(nameof(ScaleByResolution));
            MarkSaved();
        }
    }

    public bool ScaleByResolution
    {
        get => !ScaleUniform;
        set { if (value) ScaleUniform = false; else Notify(nameof(ScaleByResolution)); }
    }
    public bool LaunchAtStartup { get; set; }
    public string HotkeySwitch { get; set; } = "";
    public string HotkeyLock { get; set; } = "";
    public bool HotkeySwitchEnabled { get; set; } = true;
    public bool DeviceNumberHotkeys { get; set; } = true;
    public bool EdgeGlowEnabled { get; set; } = true;
    private string _glowHex = "#3DDC84";
    public string EdgeGlowColor
    {
        get => _glowHex;
        set
        {
            if (_glowHex == value) return;
            _glowHex = value;
            Notify(nameof(EdgeGlowColor));
            Notify(nameof(GlowColorValue));
        }
    }

    /// <summary>Colour picker binding (hue/saturation spectrum); writes the hex that is auto-saved and applied live.</summary>
    public Avalonia.Media.Color GlowColorValue
    {
        get => Avalonia.Media.Color.TryParse(_glowHex, out var c) ? c : Avalonia.Media.Color.Parse("#3DDC84");
        set => EdgeGlowColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    public static IReadOnlyList<string> GlowSwatches { get; } = new[] { "#14B8A6", "#3DDC84", "#22C55E", "#3B6CFF", "#60CDFF", "#A855F7", "#EC4899", "#EF4444", "#F59E0B", "#FFFFFF" };
    public RelayCommand<string> PickGlowColorCommand { get; } = null!;
    public decimal? EdgeGlowIntensity { get; set; } = 80;
    public decimal? EdgeGlowThickness { get; set; } = 22;
    public bool BreakthroughEnabled { get; set; } = true;
    public decimal? BreakthroughIntensity { get; set; } = 70;
    public bool ReduceMotion { get; set; }
    public bool SoundEnabled { get; set; }
    public bool RecordingHotkey { get; private set; }
    public bool InternetEnabled { get; set; }
    public bool HandoffPc { get; set; }
    public string HandoffStatus { get; private set; } = "";
    public string RelayUrl { get; set; } = "";
    public string StunHost { get; set; } = "";
    public decimal? StunPort { get; set; } = 19302;
    public string PublicHost { get; set; } = "";
    public decimal? PublicPort { get; set; }
    public bool FileTransfer { get; set; } = true;
    public string LicenseKey { get; set; } = "";
    public string ManualName { get; set; } = "";
    public string ManualAddress { get; set; } = "";
    public decimal? ManualPort { get; set; } = ProductInfo.DefaultSessionPort;

    public string EditName { get; set; } = "";
    public string EditAddress { get; set; } = "";
    public decimal? EditPort { get; set; }
    public decimal? EditWidth { get; set; }
    public decimal? EditHeight { get; set; }
    public decimal? EditOffset { get; set; }
    public decimal? EditSpan { get; set; }
    public decimal? EditCornerSize { get; set; }
    public decimal? EditSpeed { get; set; } = 1;
    public int PcEdgeIndex { get; set; } = 1;
    public int ReturnEdgeIndex { get; set; }
    public int AnchorIndex { get; set; }
    public int CornerIndex { get; set; } = 1;

    public string NavOverview { get; private set; } = "";
    public string NavDevices { get; private set; } = "";
    public string NavLayout { get; private set; } = "";
    public string NavNetwork { get; private set; } = "";
    public string NavPairing { get; private set; } = "";
    public string NavLicense { get; private set; } = "";
    public string NavAbout { get; private set; } = "";
    public string Tagline { get; private set; } = "";
    public string LanguageLabel { get; private set; } = "";
    public string EnglishLabel { get; private set; } = "";
    public string PersianLabel { get; private set; } = "";
    public string DarkLabel { get; private set; } = "";
    public string LightLabel { get; private set; } = "";
    public string DragHint { get; private set; } = "";
    public string BrandKicker { get; private set; } = "";
    public string LockButton { get; private set; } = "";
    public string SwitchButton { get; private set; } = "";
    public string EmptyDevices { get; private set; } = "";
    public string EmptyHint { get; private set; } = "";
    public string LogLabel { get; private set; } = "";
    public string DiscoveredLabel { get; private set; } = "";
    public string AddDeviceLabel { get; private set; } = "";
    public string RemoveLabel { get; private set; } = "";
    public string NameLabel { get; private set; } = "";
    public string AddressLabel { get; private set; } = "";
    public string PortLabel { get; private set; } = "";
    public string SaveLabel { get; private set; } = "";
    public string PcEdgeLabel { get; private set; } = "";
    public string ReturnEdgeLabel { get; private set; } = "";
    public string OffsetLabel { get; private set; } = "";
    public string SpanLabel { get; private set; } = "";
    public string SpeedLabel { get; private set; } = "";
    public string AnchorLabel { get; private set; } = "";
    public string CornerLabel { get; private set; } = "";
    public string CornerSizeLabel { get; private set; } = "";
    public string LeftLabel { get; private set; } = "";
    public string RightLabel { get; private set; } = "";
    public string TopLabel { get; private set; } = "";
    public string BottomLabel { get; private set; } = "";
    public string EdgeLabel { get; private set; } = "";
    public string TopLeftLabel { get; private set; } = "";
    public string TopRightLabel { get; private set; } = "";
    public string BottomLeftLabel { get; private set; } = "";
    public string BottomRightLabel { get; private set; } = "";
    public string LayoutHint { get; private set; } = "";
    public string SpeedHint { get; private set; } = "";
    public string OffsetHint { get; private set; } = "";
    public string HostScreenLabel { get; private set; } = "";
    public string ListenPortLabel { get; private set; } = "";
    public string InternetLabel { get; private set; } = "";
    public string RelayLabel { get; private set; } = "";
    public string StunLabel { get; private set; } = "";
    public string StunHint { get; private set; } = "";
    public string PublicHostLabel { get; private set; } = "";
    public string PublicHostHint { get; private set; } = "";
    public string FileTransferLabel { get; private set; } = "";
    public string EnableInternetLabel { get; private set; } = "";
    public string HandoffPcLabel { get; private set; } = "";
    public string HandoffPcHint { get; private set; } = "";
    public string ClipboardLabel { get; private set; } = "";
    public string HotkeySwitchLabel { get; private set; } = "";
    public string HotkeyLockLabel { get; private set; } = "";
    public string HotkeySwitchEnableLabel { get; private set; } = "";
    public string RecordHotkeyLabel { get; private set; } = "";
    public string DeviceNumberHotkeysLabel { get; private set; } = "";
    public string NumberHotkeysHint { get; private set; } = "";
    public string HotkeyConflict { get; private set; } = "";
    public string SwitchShortcutLine { get; private set; } = "";
    public string InputTraceLine { get; private set; } = "";
    public string TraySwitchText { get; private set; } = "";
    public string GlowEnableLabel { get; private set; } = "";
    public string GlowColorLabel { get; private set; } = "";
    public string GlowIntensityLabel { get; private set; } = "";
    public string GlowThicknessLabel { get; private set; } = "";
    public string BreakthroughLabel { get; private set; } = "";
    public string BreakthroughIntensityLabel { get; private set; } = "";
    public string ReduceMotionLabel { get; private set; } = "";
    public string SoundLabel { get; private set; } = "";
    public string SoundHint { get; private set; } = "";
    public string DiagTitle { get; private set; } = "";
    public string DiagLogLabel { get; private set; } = "";
    public string DiagPath { get; private set; } = "";
    public string DiagTail { get; private set; } = "";
    public string DiagExportLabel { get; private set; } = "";
    public string DiagPathHint { get; private set; } = "";
    public string OpenSamsungLabel { get; private set; } = "";
    public string SamsungHint { get; private set; } = "";
    public string InjectNoteText { get; private set; } = "";
    public bool HasConnectDetail { get; private set; }
    public bool HasHotkeyConflict { get; private set; }
    public string StartupLabel { get; private set; } = "";
    public string SecurityNote { get; private set; } = "";
    public string StartPairingLabel { get; private set; } = "";
    public string PinLabel { get; private set; } = "";
    public string QrHint { get; private set; } = "";
    public string CopyPinLabel { get; private set; } = "";
    public string LicenseKeyLabel { get; private set; } = "";
    public string ActivateLabel { get; private set; } = "";
    public string LicenseNote { get; private set; } = "";
    public string AboutBody { get; private set; } = "";
    public string VersionText { get; private set; } = "";
    public string BuildText { get; private set; } = "";
    public string VersionShort => ProductInfo.Version;
    public string ShowWindowText { get; private set; } = "";
    public string QuitText { get; private set; } = "";
    public string WidthLabel { get; private set; } = "";
    public string HeightLabel { get; private set; } = "";
    public string NoDeviceSelected { get; private set; } = "";
    public string InjectionHint { get; private set; } = "";
    public string Eula { get; private set; } = "";
    public string StartupHint { get; private set; } = "";
    public string HideHint { get; private set; } = "";
    public string SimulateLeaveLabel { get; private set; } = "";
    public string SimulateReturnLabel { get; private set; } = "";
    public string RawInputNote { get; private set; } = "";
    public string ManualAddLabel { get; private set; } = "";
    public string ApplyLabel { get; private set; } = "";
    public bool ShowMethodCards { get; private set; }
    public bool ShowConnectWork { get; private set; }
    public bool ShowConnectedView { get; private set; }
    public bool ShowRetry { get; private set; }
    public bool ShowLanTools { get; private set; }
    public bool ShowRelayTools { get; private set; }
    public string ActiveMethod { get; private set; } = "usb";
    public string ConnectPhase { get; private set; } = "idle";
    public string ConnectStatus { get; private set; } = "";
    public string ConnectDetail { get; private set; } = "";
    public string ConnectKind { get; private set; } = "";
    public string NavConnect { get; private set; } = "";
    public string ConnectTitle { get; private set; } = "";
    public string ConnectLead { get; private set; } = "";
    public string UsbTitle { get; private set; } = "";
    public string UsbBody { get; private set; } = "";
    public string LanTitle { get; private set; } = "";
    public string LanBody { get; private set; } = "";
    public string RelayTitle { get; private set; } = "";
    public string RelayBody { get; private set; } = "";
    public string RetryLabel { get; private set; } = "";
    public string SwitchMethodLabel { get; private set; } = "";
    public string ConnectedTitle { get; private set; } = "";
    public string LatencyLabel { get; private set; } = "";
    public string TypeLabel { get; private set; } = "";

    public void Pull()
    {
        var host = _runtime.Host;
        var lang = host.Settings.Language;
        var snaps = host.SnapshotDevices();
        LayoutDevices = snaps;
        ActiveDeviceId = host.Machine.ActiveDeviceId;
        Monitors = host.SnapshotMonitors();
        ArrangeNote = BuildArrangeNote(host, lang);
        var seen = snaps.Select(s => s.Id).ToHashSet();
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Devices[i].Id))
                Devices.RemoveAt(i);
        }
        foreach (var snap in snaps)
        {
            var row = Devices.FirstOrDefault(d => d.Id == snap.Id);
            if (row == null)
            {
                row = new DeviceRow(snap.Id);
                Devices.Add(row);
            }
            row.Apply(snap, lang, PathName(lang, snap.Path));
        }
        NoDevices = Devices.Count == 0;
        var peers = host.SnapshotDiscovered();
        if (peers.Count != Peers.Count || peers.Zip(Peers).Any(p => p.First.Id != p.Second.Id))
        {
            Peers.Clear();
            foreach (var peer in peers)
                Peers.Add(new PeerRow(peer));
        }
        Logs.Clear();
        foreach (var line in host.SnapshotLogs().TakeLast(12))
            Logs.Add(line);
        var active = snaps.FirstOrDefault(s => s.Active);
        StatusText = host.Machine.Locked
            ? Catalog.Get(lang, "statusLocked")
            : active == null
                ? Catalog.Get(lang, "statusIdle")
                : Catalog.Format(lang, "statusControlling", active.Name);
        ListenText = string.IsNullOrWhiteSpace(_runtime.BindError)
            ? Catalog.Format(lang, "listeningOn", _runtime.BoundPort)
            : _runtime.BindError;
        HasBindError = !string.IsNullOrWhiteSpace(_runtime.BindError);
        SwitchShortcutLine = Catalog.Format(lang, "switchShortcut", host.Settings.HotkeySwitch);
        InputTraceLine = Catalog.Format(lang, "inputTrace", host.ButtonsSent, host.WheelsSent);
        TraySwitchText = Catalog.Format(lang, "traySwitch", host.Settings.HotkeySwitch);
        DiagPath = DiagLog.FilePath;
        DiagTail = DiagLog.Tail(6);
        InjectNoteText = string.IsNullOrWhiteSpace(_runtime.InjectNote) ? "" : Catalog.Get(lang, _runtime.InjectNote);
        ActiveText = active?.Name ?? Catalog.Get(lang, "controlOnPc");
        LockText = host.Machine.Locked ? Catalog.Get(lang, "lockedState") : Catalog.Get(lang, "unlockedState");
        LockButton = host.Machine.Locked ? Catalog.Get(lang, "unlock") : Catalog.Get(lang, "lock");
        var rtt = snaps.Select(s => s.RttMs).FirstOrDefault(v => v.HasValue);
        RttText = rtt.HasValue ? $"{rtt.Value:0} {Catalog.Get(lang, "ms")}" : "—";
        var handoffKey = _runtime.HandoffStatus;
        var handoffLine = string.IsNullOrEmpty(handoffKey) ? "" : Catalog.Get(lang, handoffKey);
        if (handoffLine != HandoffStatus)
        {
            HandoffStatus = handoffLine;
            Notify(nameof(HandoffStatus));
        }
        PacketText = host.PacketsSent.ToString(CultureInfo.InvariantCulture);
        PathText = active == null ? Catalog.Get(lang, "pathOffline") : PathName(lang, active.Path);
        if (!string.IsNullOrWhiteSpace(_runtime.RelayState) && host.Settings.InternetEnabled)
            PathText += " · " + _runtime.RelayState;
        var license = host.License;
        LicenseText = license.Code switch
        {
            "licensed" => license.Edition == LicenseEdition.Family ? Catalog.Get(lang, "editionFamily") : Catalog.Get(lang, "editionPro"),
            "expired" => Catalog.Get(lang, "trialExpired"),
            "invalid" => Catalog.Get(lang, "invalidKey"),
            _ => Catalog.Format(lang, "trialActive", license.DaysLeft)
        };
        ProblemText = string.Join(" ", host.LayoutProblems);
        HasProblems = host.LayoutProblems.Count > 0;
        PinText = host.CurrentPin;
        PairingVisible = host.PairingOpen;
        PairingState = host.PairingOpen ? Catalog.Get(lang, "pairingOpen") : "";
        var uri = _runtime.ExtendedPairingUri();
        if (host.PairingOpen && uri != _qrText)
        {
            _qrText = uri;
            QrImage = QrImages.Make(uri);
            Notify(nameof(QrImage));
        }
        if (!host.PairingOpen && _qrText.Length > 0)
        {
            _qrText = "";
            QrImage = null;
            Notify(nameof(QrImage));
        }
        ActiveMethod = string.IsNullOrWhiteSpace(_runtime.ActiveMethod) ? "usb" : _runtime.ActiveMethod;
        ConnectPhase = string.IsNullOrWhiteSpace(_runtime.ConnectPhase) ? "idle" : _runtime.ConnectPhase;
        var detailKey = _runtime.ConnectDetail;
        ConnectDetail = string.IsNullOrWhiteSpace(detailKey) ? "" : Catalog.Get(lang, detailKey);
        ConnectStatus = ConnectPhase switch
        {
            "connected" => Catalog.Get(lang, "connectedTitle"),
            "connecting" => Catalog.Get(lang, "usbConnecting"),
            "disconnected" => Catalog.Get(lang, "usbDisconnected"),
            "reconnecting" => Catalog.Get(lang, "usbReconnecting"),
            "needDebug" => Catalog.Get(lang, "usbNeedDebug"),
            "unauthorized" => Catalog.Get(lang, "usbUnauthorized"),
            "offline" => Catalog.Get(lang, "usbOffline"),
            "failed" => string.IsNullOrWhiteSpace(ConnectDetail) ? Catalog.Get(lang, "connectFailed") : ConnectDetail,
            "searching" => ActiveMethod switch
            {
                "usb" => Catalog.Get(lang, "usbSearching"),
                "relay" => Catalog.Get(lang, "waitingRelay"),
                _ => Catalog.Get(lang, "waitingPhone")
            },
            _ => Catalog.Get(lang, "connectTitle")
        };
        HasConnectDetail = !string.IsNullOrWhiteSpace(ConnectDetail) && ConnectDetail != ConnectStatus;
        ConnectKind = ActiveMethod switch
        {
            "usb" => Catalog.Get(lang, "typeUsb"),
            "relay" => Catalog.Get(lang, "typeRelay"),
            "lan" => Catalog.Get(lang, "typeLan"),
            _ => ""
        };
        ShowMethodCards = string.IsNullOrEmpty(_runtime.ActiveMethod);
        ShowConnectWork = !ShowMethodCards && ConnectPhase != "connected";
        ShowConnectedView = ConnectPhase == "connected" && !string.IsNullOrEmpty(_runtime.ActiveMethod);
        ShowRetry = ConnectPhase is "failed" or "needDebug" or "unauthorized" or "offline";
        ShowLanTools = _runtime.ActiveMethod == "lan" && ConnectPhase != "connected";
        ShowRelayTools = _runtime.ActiveMethod == "relay" && ConnectPhase != "connected";
        NotifyMany(
            nameof(LayoutDevices), nameof(ActiveDeviceId), nameof(Monitors), nameof(SelectedDeviceId), nameof(ArrangeNote), nameof(HasArrangeNote),
            nameof(StatusText), nameof(ListenText),
            nameof(ActiveText), nameof(LockText), nameof(LockButton), nameof(RttText), nameof(PacketText),
            nameof(PathText), nameof(LicenseText), nameof(ProblemText), nameof(HasProblems), nameof(PinText),
            nameof(PairingVisible), nameof(PairingState), nameof(NoDevices), nameof(HasBindError),
            nameof(ActiveMethod), nameof(ConnectPhase), nameof(ConnectStatus), nameof(ConnectDetail), nameof(HasConnectDetail), nameof(ConnectKind),
            nameof(SwitchShortcutLine), nameof(InputTraceLine), nameof(TraySwitchText), nameof(DiagPath), nameof(DiagTail), nameof(InjectNoteText),
            nameof(ShowMethodCards), nameof(ShowConnectWork), nameof(ShowConnectedView), nameof(ShowRetry),
            nameof(ShowLanTools), nameof(ShowRelayTools));
    }

    public void NoteClipboardWrite() => _suppressClipboard = true;

    public bool NativeClipboard => _runtime.NativeClipboard;

    public bool ConsumeClipboardSuppression()
    {
        if (!_suppressClipboard)
            return false;
        _suppressClipboard = false;
        return true;
    }

    private void ApplyLanguage()
    {
        var lang = _runtime.Host.Settings.Language;
        string T(string key) => Catalog.Get(lang, key);
        NavConnect = T("navConnect");
        ConnectTitle = T("connectTitle");
        ConnectLead = T("connectLead");
        UsbTitle = T("methodUsb");
        UsbBody = T("methodUsbBody");
        LanTitle = T("methodLan");
        LanBody = T("methodLanBody");
        RelayTitle = T("methodRelay");
        RelayBody = T("methodRelayBody");
        RetryLabel = T("connectRetry");
        SwitchMethodLabel = T("switchMethod");
        ConnectedTitle = T("connectedTitle");
        LatencyLabel = T("latency");
        TypeLabel = T("connectionType");
        NavOverview = T("overview");
        NavDevices = T("devices");
        NavLayout = T("layout");
        NavNetwork = T("network");
        NavPairing = T("pairing");
        NavLicense = T("license");
        NavAbout = T("about");
        Tagline = T("tagline");
        LanguageLabel = T("language");
        EnglishLabel = T("english");
        PersianLabel = T("persian");
        DarkLabel = T("themeDark");
        LightLabel = T("themeLight");
        DragHint = T("dragHint");
        BrandKicker = T("brandKicker");
        SwitchButton = T("switchNext");
        EmptyDevices = T("emptyDevices");
        EmptyHint = T("emptyHint");
        LogLabel = T("log");
        DiscoveredLabel = T("discovered");
        AddDeviceLabel = T("addDevice");
        RemoveLabel = T("remove");
        NameLabel = T("name");
        AddressLabel = T("address");
        PortLabel = T("port");
        SaveLabel = T("save");
        PcEdgeLabel = T("pcEdge");
        ReturnEdgeLabel = T("returnEdge");
        OffsetLabel = T("offset");
        SpanLabel = T("span");
        SpeedLabel = T("pointerSpeed");
        AnchorLabel = T("anchor");
        CornerLabel = T("corner");
        CornerSizeLabel = T("cornerSize");
        LeftLabel = T("left");
        RightLabel = T("right");
        TopLabel = T("top");
        BottomLabel = T("bottom");
        EdgeLabel = T("edge");
        TopLeftLabel = T("topLeft");
        TopRightLabel = T("topRight");
        BottomLeftLabel = T("bottomLeft");
        BottomRightLabel = T("bottomRight");
        LayoutHint = T("layoutHint");
        SpeedHint = T("speedHint");
        OffsetHint = T("offsetHint");
        HostScreenLabel = T("hostScreen");
        ListenPortLabel = T("listenPort");
        InternetLabel = T("internetMode");
        RelayLabel = T("relayUrl");
        StunLabel = T("stunServer");
        StunHint = T("stunHint");
        PublicHostLabel = T("publicHost");
        PublicHostHint = T("publicHostHint");
        EnableInternetLabel = T("enableInternet");
        HandoffPcLabel = T("handoffPc");
        HandoffPcHint = T("handoffPcHint");
        ClipboardLabel = T("clipboardShare");
        FileTransferLabel = T("fileTransfer");
        HotkeySwitchLabel = T("hotkeySwitch");
        HotkeyLockLabel = T("hotkeyLock");
        HotkeySwitchEnableLabel = T("hotkeySwitchEnable");
        RecordHotkeyLabel = RecordingHotkey ? T("hotkeyRecording") : T("hotkeyRecord");
        DeviceNumberHotkeysLabel = T("deviceNumberHotkeys");
        NumberHotkeysHint = T("numberHotkeysHint");
        GlowEnableLabel = T("glowEnable");
        GlowColorLabel = T("glowColor");
        GlowIntensityLabel = T("glowIntensity");
        GlowThicknessLabel = T("glowThickness");
        BreakthroughLabel = T("breakthrough");
        BreakthroughIntensityLabel = T("breakthroughIntensity");
        ReduceMotionLabel = T("reduceMotion");
        SoundLabel = T("soundEnabled");
        SoundHint = T("soundHint");
        DiagTitle = T("diagTitle");
        DiagLogLabel = T("diagLog");
        DiagExportLabel = T("diagExport");
        DiagPath = DiagLog.FilePath;
        DiagPathHint = T("diagPathHint");
        OpenSamsungLabel = T("openSamsungDriver");
        SamsungHint = T("usbSamsungHint");
        SwitchShortcutLine = Catalog.Format(_runtime.Host.Settings.Language, "switchShortcut", _runtime.Host.Settings.HotkeySwitch);
        InputTraceLine = Catalog.Format(_runtime.Host.Settings.Language, "inputTrace", _runtime.Host.ButtonsSent, _runtime.Host.WheelsSent);
        TraySwitchText = Catalog.Format(_runtime.Host.Settings.Language, "traySwitch", _runtime.Host.Settings.HotkeySwitch);
        StartupLabel = T("launchAtStartup");
        SecurityNote = T("securityNote");
        StartPairingLabel = T("startPairing");
        PinLabel = T("pinLabel");
        QrHint = T("qrHint");
        CopyPinLabel = T("copyPin");
        LicenseKeyLabel = T("licenseKey");
        ActivateLabel = T("activate");
        LicenseNote = T("licenseNote");
        AboutBody = T("aboutBody");
        VersionText = $"{T("versionLabel")} {ProductInfo.Version}  ·  {T("versionBuild")} {ProductInfo.VersionCode}";
        BuildText = $"{T("buildDateLabel")} {ProductInfo.BuildDate}  ·  {T("protocolVersion")} {ProductInfo.ProtocolVersion}";
        ShowWindowText = T("showWindow");
        QuitText = T("quit");
        WidthLabel = T("width");
        HeightLabel = T("height");
        NoDeviceSelected = T("noDeviceSelected");
        InjectionHint = T("injectionHint");
        Eula = T("eulaShort");
        StartupHint = T("startupHint");
        HideHint = T("hideHint");
        SimulateLeaveLabel = T("simulateCross");
        SimulateReturnLabel = T("simulateBack");
        RawInputNote = T("rawInputNote");
        ManualAddLabel = T("manualAdd");
        ApplyLabel = T("apply");
        NotifyMany(
            nameof(NavConnect), nameof(ConnectTitle), nameof(ConnectLead), nameof(UsbTitle), nameof(UsbBody),
            nameof(LanTitle), nameof(LanBody), nameof(RelayTitle), nameof(RelayBody), nameof(RetryLabel),
            nameof(SwitchMethodLabel), nameof(ConnectedTitle), nameof(LatencyLabel), nameof(TypeLabel),
            nameof(NavOverview), nameof(NavDevices), nameof(NavLayout), nameof(NavNetwork), nameof(NavPairing),
            nameof(NavLicense), nameof(NavAbout), nameof(Tagline), nameof(LanguageLabel), nameof(EnglishLabel),
            nameof(PersianLabel), nameof(DarkLabel), nameof(LightLabel), nameof(DragHint), nameof(BrandKicker), nameof(LockButton), nameof(SwitchButton), nameof(EmptyDevices), nameof(EmptyHint), nameof(LogLabel),
            nameof(DiscoveredLabel), nameof(AddDeviceLabel), nameof(RemoveLabel), nameof(NameLabel), nameof(AddressLabel),
            nameof(PortLabel), nameof(SaveLabel), nameof(PcEdgeLabel), nameof(ReturnEdgeLabel), nameof(OffsetLabel),
            nameof(SpanLabel), nameof(SpeedLabel), nameof(AnchorLabel), nameof(CornerLabel), nameof(CornerSizeLabel),
            nameof(LeftLabel), nameof(RightLabel), nameof(TopLabel), nameof(BottomLabel), nameof(EdgeLabel),
            nameof(TopLeftLabel), nameof(TopRightLabel), nameof(BottomLeftLabel), nameof(BottomRightLabel),
            nameof(LayoutHint), nameof(SpeedHint), nameof(OffsetHint), nameof(HostScreenLabel), nameof(ListenPortLabel),
            nameof(InternetLabel), nameof(RelayLabel), nameof(StunLabel), nameof(StunHint), nameof(PublicHostLabel), nameof(PublicHostHint), nameof(EnableInternetLabel), nameof(HandoffPcLabel), nameof(HandoffPcHint), nameof(ClipboardLabel), nameof(FileTransferLabel),
            nameof(HotkeySwitchLabel), nameof(HotkeyLockLabel), nameof(HotkeySwitchEnableLabel), nameof(RecordHotkeyLabel),
            nameof(DeviceNumberHotkeysLabel), nameof(NumberHotkeysHint), nameof(SwitchShortcutLine), nameof(InputTraceLine), nameof(TraySwitchText),
            nameof(GlowEnableLabel), nameof(GlowColorLabel), nameof(GlowIntensityLabel), nameof(GlowThicknessLabel),
            nameof(BreakthroughLabel), nameof(BreakthroughIntensityLabel), nameof(ReduceMotionLabel), nameof(SoundLabel), nameof(SoundHint),
            nameof(DiagTitle), nameof(DiagLogLabel), nameof(DiagExportLabel), nameof(DiagPath), nameof(DiagPathHint), nameof(OpenSamsungLabel), nameof(SamsungHint),
            nameof(StartupLabel), nameof(SecurityNote),
            nameof(StartPairingLabel), nameof(PinLabel), nameof(QrHint), nameof(CopyPinLabel), nameof(LicenseKeyLabel),
            nameof(ActivateLabel), nameof(LicenseNote), nameof(AboutBody), nameof(VersionText), nameof(BuildText), nameof(ShowWindowText),
            nameof(QuitText), nameof(WidthLabel), nameof(HeightLabel), nameof(NoDeviceSelected), nameof(InjectionHint),
            nameof(Eula), nameof(StartupHint), nameof(HideHint), nameof(SimulateLeaveLabel), nameof(SimulateReturnLabel),
            nameof(RawInputNote), nameof(ManualAddLabel), nameof(ApplyLabel));
        Ui = new UiText(lang);
        NotifyMany(nameof(Ui), nameof(PageTitle), nameof(LanguageIndex));
    }

    private void LoadSettingsIntoForm()
    {
        var s = _runtime.Host.Settings;
        DisplayName = s.DisplayName;
        ListenPort = s.ListenPort;
        ClipboardShare = s.ClipboardShare;
        FileTransfer = s.FileTransfer;
        LaunchAtStartup = s.LaunchAtStartup;
        HotkeySwitch = s.HotkeySwitch;
        HotkeyLock = s.HotkeyLock;
        HotkeySwitchEnabled = s.HotkeySwitchEnabled;
        DeviceNumberHotkeys = s.DeviceNumberHotkeys;
        EdgeGlowEnabled = s.EdgeGlowEnabled;
        EdgeGlowColor = s.EdgeGlowColor;
        EdgeGlowIntensity = s.EdgeGlowIntensity;
        EdgeGlowThickness = s.EdgeGlowThickness;
        BreakthroughEnabled = s.BreakthroughEnabled;
        BreakthroughIntensity = s.BreakthroughIntensity;
        ReduceMotion = s.ReduceMotion;
        SoundEnabled = s.SoundEnabled;
        InternetEnabled = s.InternetEnabled;
        HandoffPc = s.Handoff is "pc" or "both";
        RelayUrl = s.RelayUrl;
        StunHost = s.StunHost;
        StunPort = s.StunPort;
        PublicHost = s.PublicHost;
        PublicPort = s.PublicPort;
        LicenseKey = s.LicenseKey ?? "";
        NotifyMany(nameof(DisplayName), nameof(ListenPort), nameof(ClipboardShare), nameof(FileTransfer), nameof(LaunchAtStartup),
            nameof(HotkeySwitch), nameof(HotkeyLock), nameof(HotkeySwitchEnabled), nameof(DeviceNumberHotkeys),
            nameof(EdgeGlowEnabled), nameof(EdgeGlowColor), nameof(EdgeGlowIntensity), nameof(EdgeGlowThickness),
            nameof(BreakthroughEnabled), nameof(BreakthroughIntensity), nameof(ReduceMotion), nameof(SoundEnabled),
            nameof(InternetEnabled), nameof(HandoffPc), nameof(RelayUrl), nameof(StunHost),
            nameof(StunPort), nameof(PublicHost), nameof(PublicPort), nameof(LicenseKey));
    }

    private static string BuildArrangeNote(EdgeSpan.Session.HostController host, string lang)
    {
        var monitors = host.Machine.Monitors;
        foreach (var p in host.Machine.Devices)
        {
            if (host.Machine.NeighborFor(p) is not { } next)
                continue;
            var a = Index(monitors, host.Machine.MonitorFor(p)) + 1;
            var b = Index(monitors, next) + 1;
            return lang == "fa"
                ? $"{p.Name} بین نمایشگر {a} و {b} قرار دارد: در این بخش از لبه، نشانگر به‌جای رفتن مستقیم به نمایشگر {b} از {p.Name} عبور می‌کند."
                : $"{p.Name} sits between monitor {a} and {b}: on that part of the edge the cursor no longer goes straight to monitor {b}; it passes through {p.Name} instead.";
        }
        return "";
    }

    private static int Index(IReadOnlyList<EdgeSpan.Layout.HostMonitor> all, EdgeSpan.Layout.HostMonitor m)
    {
        for (var i = 0; i < all.Count; i++)
            if (all[i] == m)
                return i;
        return 0;
    }

    public void PlaceDevice(string id, string edge, string anchor, string corner, int offset) =>
        PlaceDevice(id, null, edge, anchor, corner, offset);

    public void PlaceDevice(string id, string? monitor, string edge, string anchor, string corner, int offset)
    {
        var current = _runtime.Host.Settings.Devices.FirstOrDefault(d => d.Id == id);
        if (current == null)
            return;
        if (monitor != null)
            current.Monitor = monitor;
        current.PcEdge = edge;
        current.Anchor = anchor;
        current.Corner = corner;
        current.OffsetPx = Math.Clamp(offset, 0, 20000);
        current.ReturnEdge = edge switch
        {
            "Left" => "Right",
            "Right" => "Left",
            "Top" => "Bottom",
            _ => "Top"
        };
        _runtime.Host.UpdateDevice(current);
        _loadedId = "";
        if (_selected?.Id == id)
            LoadEditor();
        Pull();
    }

    private void ApplyShotOverrides()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_DEMO"), "1", StringComparison.Ordinal)
            && _runtime.Host.Settings.Devices.Count == 0)
            _runtime.Host.AddManual("Tablet", "192.168.1.40", ProductInfo.DefaultSessionPort);
        var lang = Environment.GetEnvironmentVariable("EDGESPAN_LANG");
        if (lang is "en" or "fa")
            SetLanguage(lang);
        var theme = Environment.GetEnvironmentVariable("EDGESPAN_THEME");
        if (theme is "light" or "dark")
            SetTheme(theme == "dark");
        if (string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_PAIR"), "1", StringComparison.Ordinal))
        {
            StartPairing();
            var pinFile = Environment.GetEnvironmentVariable("EDGESPAN_PIN_FILE");
            if (!string.IsNullOrWhiteSpace(pinFile))
            {
                try { File.WriteAllText(pinFile, _runtime.Host.CurrentPin); }
                catch (IOException) { }
            }
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("EDGESPAN_PAGE"), out var page))
            Page = Math.Clamp(page, 0, 8);
    }

    private void SetTheme(bool dark) => SetThemeMode(dark ? "dark" : "light");

    private void SetThemeMode(string mode)
    {
        if (mode != "system")
            IsDark = mode == "dark";
        _runtime.Host.Settings.Theme = mode;
        _runtime.Host.Save();
        Notify(nameof(IsDark));
        Notify(nameof(FollowSystemTheme));
        Notify(nameof(ThemeIndex));
        ThemeChanged?.Invoke();
    }

    private void SetLanguage(string lang)
    {
        _runtime.Host.Settings.Language = lang;
        _runtime.Host.Save();
        ApplyLanguage();
        LoadSettingsIntoForm();
        Pull();
        LanguageChanged?.Invoke();
    }

    private void SaveSettings()
    {
        var s = _runtime.Host.Settings;
        s.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? s.DisplayName : DisplayName.Trim();
        s.ListenPort = ClampPort(ListenPort, s.ListenPort);
        s.ClipboardShare = ClipboardShare;
        s.FileTransfer = FileTransfer;
        s.LaunchAtStartup = LaunchAtStartup;
        var nextSwitch = string.IsNullOrWhiteSpace(HotkeySwitch) ? s.HotkeySwitch : HotkeySwitch.Trim();
        var nextLock = string.IsNullOrWhiteSpace(HotkeyLock) ? s.HotkeyLock : HotkeyLock.Trim();
        if (HotkeyPolicy.Conflicts(nextSwitch, nextLock, DeviceNumberHotkeys))
        {
            HotkeyConflict = Catalog.Get(s.Language, "hotkeyConflict");
            HasHotkeyConflict = true;
            HotkeySwitch = s.HotkeySwitch;
            HotkeyLock = s.HotkeyLock;
            Notify(nameof(HotkeySwitch));
            Notify(nameof(HotkeyLock));
        }
        else
        {
            s.HotkeySwitch = nextSwitch;
            s.HotkeyLock = nextLock;
            HotkeyConflict = "";
            HasHotkeyConflict = false;
        }
        s.HotkeySwitchEnabled = HotkeySwitchEnabled;
        s.DeviceNumberHotkeys = DeviceNumberHotkeys;
        s.EdgeGlowEnabled = EdgeGlowEnabled;
        s.EdgeGlowColor = string.IsNullOrWhiteSpace(EdgeGlowColor) ? "#3DDC84" : EdgeGlowColor.Trim();
        s.EdgeGlowIntensity = (int)Math.Clamp(EdgeGlowIntensity ?? s.EdgeGlowIntensity, 0, 100);
        s.EdgeGlowThickness = (int)Math.Clamp(EdgeGlowThickness ?? s.EdgeGlowThickness, 4, 120);
        s.BreakthroughEnabled = BreakthroughEnabled;
        s.BreakthroughIntensity = (int)Math.Clamp(BreakthroughIntensity ?? s.BreakthroughIntensity, 0, 100);
        s.ReduceMotion = ReduceMotion;
        s.SoundEnabled = SoundEnabled;
        EdgeGlowColor = s.EdgeGlowColor;
        EdgeGlowIntensity = s.EdgeGlowIntensity;
        EdgeGlowThickness = s.EdgeGlowThickness;
        BreakthroughIntensity = s.BreakthroughIntensity;
        s.InternetEnabled = InternetEnabled;
        s.Handoff = HandoffPc ? "pc" : "off";
        s.RelayUrl = string.IsNullOrWhiteSpace(RelayUrl) ? s.RelayUrl : RelayUrl.Trim();
        s.StunHost = string.IsNullOrWhiteSpace(StunHost) ? "" : StunHost.Trim();
        if (string.Equals(s.StunHost, "stun.l.google.com", StringComparison.OrdinalIgnoreCase))
            s.StunHost = "";
        s.StunPort = (int)Math.Clamp(StunPort ?? s.StunPort, 1, 65535);
        s.PublicHost = string.IsNullOrWhiteSpace(PublicHost) ? "" : PublicHost.Trim();
        s.PublicPort = (int)Math.Clamp(PublicPort ?? 0, 0, 65535);
        _runtime.Host.Save();
        _runtime.NotifySettingsChanged();
        SavedText = HasHotkeyConflict ? HotkeyConflict : Catalog.Get(s.Language, "settingsSaved");
        SwitchShortcutLine = Catalog.Format(s.Language, "switchShortcut", s.HotkeySwitch);
        InputTraceLine = Catalog.Format(s.Language, "inputTrace", _runtime.Host.ButtonsSent, _runtime.Host.WheelsSent);
        TraySwitchText = Catalog.Format(s.Language, "traySwitch", s.HotkeySwitch);
        Notify(nameof(SavedText));
        Notify(nameof(HotkeyConflict));
        Notify(nameof(HasHotkeyConflict));
        Notify(nameof(SwitchShortcutLine));
        Notify(nameof(InputTraceLine));
        Notify(nameof(TraySwitchText));
        Notify(nameof(EdgeGlowColor));
        Notify(nameof(EdgeGlowIntensity));
        Notify(nameof(EdgeGlowThickness));
        Notify(nameof(BreakthroughIntensity));
        ChromeChanged?.Invoke();
        Pull();
    }

    public void BeginHotkeyCapture()
    {
        RecordingHotkey = true;
        _runtime.Host.HotkeyCapture = true;
        RecordHotkeyLabel = Catalog.Get(_runtime.Host.Settings.Language, "hotkeyRecording");
        Notify(nameof(RecordingHotkey));
        Notify(nameof(RecordHotkeyLabel));
    }

    public void CommitHotkey(string chord)
    {
        RecordingHotkey = false;
        _runtime.Host.HotkeyCapture = false;
        HotkeySwitch = chord;
        var lang = _runtime.Host.Settings.Language;
        if (HotkeyPolicy.Conflicts(chord, HotkeyLock, DeviceNumberHotkeys))
        {
            HotkeyConflict = Catalog.Get(lang, "hotkeyConflict");
            HasHotkeyConflict = true;
        }
        else
        {
            HotkeyConflict = "";
            HasHotkeyConflict = false;
        }
        RecordHotkeyLabel = Catalog.Get(lang, "hotkeyRecord");
        Notify(nameof(RecordingHotkey));
        Notify(nameof(RecordHotkeyLabel));
        Notify(nameof(HotkeySwitch));
        Notify(nameof(HotkeyConflict));
        Notify(nameof(HasHotkeyConflict));
    }

    public void CancelHotkeyCapture()
    {
        RecordingHotkey = false;
        _runtime.Host.HotkeyCapture = false;
        RecordHotkeyLabel = Catalog.Get(_runtime.Host.Settings.Language, "hotkeyRecord");
        Notify(nameof(RecordingHotkey));
        Notify(nameof(RecordHotkeyLabel));
    }

    private void OpenSamsung()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProductInfo.SamsungUsbDriverUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ExportLog()
    {
        var tail = DiagLog.Tail(200);
        var body = DiagLog.FilePath + Environment.NewLine + Environment.NewLine + (string.IsNullOrWhiteSpace(tail) ? "(empty)" : tail);
        CopyRequested?.Invoke(body);
    }

    private void LoadEditor()
    {
        var snap = _runtime.Host.SnapshotDevices().FirstOrDefault(d => d.Id == _selected?.Id);
        _loadedId = snap?.Id ?? "";
        HasEditor = snap != null;
        if (snap == null)
        {
            Notify(nameof(HasEditor));
            return;
        }
        EditName = snap.Name;
        EditAddress = snap.Address;
        EditPort = snap.Port;
        EditWidth = snap.ScreenW;
        EditHeight = snap.ScreenH;
        EditOffset = snap.OffsetPx;
        EditSpan = snap.SpanPx;
        EditCornerSize = snap.CornerSizePx;
        EditSpeed = (decimal)snap.PointerSpeed;
        PcEdgeIndex = EdgeIndex(snap.PcEdge);
        ReturnEdgeIndex = EdgeIndex(snap.ReturnEdge);
        AnchorIndex = string.Equals(snap.Anchor, "Corner", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CornerIndex = snap.Corner switch
        {
            "TopLeft" => 0,
            "BottomLeft" => 2,
            "BottomRight" => 3,
            _ => 1
        };
        NotifyMany(nameof(HasEditor), nameof(EditName), nameof(EditAddress), nameof(EditPort), nameof(EditWidth),
            nameof(EditHeight), nameof(EditOffset), nameof(EditSpan), nameof(EditCornerSize), nameof(EditSpeed),
            nameof(PcEdgeIndex), nameof(ReturnEdgeIndex), nameof(AnchorIndex), nameof(CornerIndex));
    }

    private void SaveDevice()
    {
        if (_selected == null)
            return;
        var current = _runtime.Host.Settings.Devices.FirstOrDefault(d => d.Id == _selected.Id);
        if (current == null)
            return;
        _runtime.Host.UpdateDevice(new DeviceConfig
        {
            Id = current.Id,
            Name = string.IsNullOrWhiteSpace(EditName) ? current.Name : EditName.Trim(),
            Address = EditAddress.Trim(),
            Port = ClampPort(EditPort, current.Port),
            ScreenWidth = (int)Math.Clamp(EditWidth ?? current.ScreenWidth, 1, 10000),
            ScreenHeight = (int)Math.Clamp(EditHeight ?? current.ScreenHeight, 1, 10000),
            PcEdge = EdgeName(PcEdgeIndex),
            ReturnEdge = EdgeName(ReturnEdgeIndex),
            Anchor = AnchorIndex == 1 ? "Corner" : "Edge",
            Corner = CornerIndex switch { 0 => "TopLeft", 2 => "BottomLeft", 3 => "BottomRight", _ => "TopRight" },
            OffsetPx = (int)Math.Clamp(EditOffset ?? 0, 0, 20000),
            SpanPx = (int)Math.Clamp(EditSpan ?? 0, 0, 20000),
            CornerSizePx = (int)Math.Clamp(EditCornerSize ?? 120, 20, 2000),
            PointerSpeed = (double)Math.Clamp(EditSpeed ?? 1, 0.1m, 8m)
        });
        SavedText = Catalog.Get(_runtime.Host.Settings.Language, "settingsSaved");
        Notify(nameof(SavedText));
        _loadedId = "";
        LoadEditor();
        Pull();
    }

    private void RemoveSelected()
    {
        if (_selected == null)
            return;
        _runtime.Host.RemoveDevice(_selected.Id);
        _selected = null;
        _loadedId = "";
        HasEditor = false;
        Notify(nameof(SelectedDevice));
        Notify(nameof(HasEditor));
        Pull();
    }

    private void AddManual()
    {
        var name = string.IsNullOrWhiteSpace(ManualName) ? "Android" : ManualName.Trim();
        var address = string.IsNullOrWhiteSpace(ManualAddress) ? "127.0.0.1" : ManualAddress.Trim();
        _runtime.Host.AddManual(name, address, ClampPort(ManualPort, ProductInfo.DefaultSessionPort));
        ManualName = "";
        ManualAddress = "";
        Notify(nameof(ManualName));
        Notify(nameof(ManualAddress));
        Pull();
    }

    private void AddPeer(PeerRow? peer)
    {
        if (peer == null)
            return;
        _runtime.Host.AddManual(peer.Name, peer.Address, peer.Port);
        Pull();
    }

    private void ChooseRelay()
    {
        var s = _runtime.Host.Settings;
        if (!string.IsNullOrWhiteSpace(RelayUrl))
            s.RelayUrl = RelayUrl.Trim();
        s.StunHost = string.IsNullOrWhiteSpace(StunHost) ? "" : StunHost.Trim();
        if (string.Equals(s.StunHost, "stun.l.google.com", StringComparison.OrdinalIgnoreCase))
            s.StunHost = "";
        s.StunPort = (int)Math.Clamp(StunPort ?? s.StunPort, 1, 65535);
        s.PublicHost = string.IsNullOrWhiteSpace(PublicHost) ? "" : PublicHost.Trim();
        s.PublicPort = (int)Math.Clamp(PublicPort ?? 0, 0, 65535);
        s.InternetEnabled = true;
        _runtime.Host.Save();
        _runtime.BeginRelay();
        Pull();
    }

    private void RetryConnect()
    {
        switch (_runtime.ActiveMethod)
        {
            case "usb": _runtime.BeginUsb(); break;
            case "relay": _runtime.BeginRelay(); break;
            default: _runtime.BeginLan(); break;
        }
        Pull();
    }

    private void StartPairing()
    {
        _runtime.Host.OpenPairingWindow();
        Page = 4;
        Pull();
    }

    private void CopyPin()
    {
        if (!string.IsNullOrWhiteSpace(PinText))
            CopyRequested?.Invoke(PinText);
        SavedText = Catalog.Get(_runtime.Host.Settings.Language, "copied");
        Notify(nameof(SavedText));
    }

    private void Activate()
    {
        _runtime.Host.Settings.LicenseKey = string.IsNullOrWhiteSpace(LicenseKey) ? null : LicenseKey.Trim();
        _runtime.Host.Save();
        var lang = _runtime.Host.Settings.Language;
        SavedText = _runtime.Host.License.IsValid && _runtime.Host.License.Code == "licensed"
            ? Catalog.Get(lang, "activated")
            : Catalog.Get(lang, "invalidKey");
        Notify(nameof(SavedText));
        Pull();
    }

    private void SimulateLeave()
    {
        var id = _selected?.Id ?? _runtime.Host.Settings.Devices.FirstOrDefault()?.Id;
        if (id != null && _runtime.Host.TryOutsidePoint(id, out var x, out var y))
            _runtime.Host.OnPhysicalMouse(x, y, 0, 0);
        Pull();
    }

    private void SimulateReturn()
    {
        if (_runtime.Host.Machine.ActiveDeviceId == null)
            return;
        _runtime.Host.OnPhysicalMouse(0, 0, -80, -80);
        if (_runtime.Host.Machine.ActiveDeviceId == null)
        {
            Pull();
            return;
        }
        _runtime.Host.OnPhysicalMouse(0, 0, 80, 80);
        foreach (var (dx, dy) in new[] { (-8000, 0), (8000, 0), (0, -8000), (0, 8000) })
        {
            if (_runtime.Host.Machine.ActiveDeviceId == null)
                break;
            _runtime.Host.OnPhysicalMouse(0, 0, dx, dy);
        }
        Pull();
    }

    public void LoadFormOnce() => LoadSettingsIntoForm();

    private static int ClampPort(decimal? value, int fallback)
    {
        var port = (int)(value ?? fallback);
        return port is < 1 or > 65535 ? fallback : port;
    }

    private static int EdgeIndex(string edge) => edge switch
    {
        "Left" => 0,
        "Top" => 2,
        "Bottom" => 3,
        _ => 1
    };

    private static string EdgeName(int index) => index switch
    {
        0 => "Left",
        2 => "Top",
        3 => "Bottom",
        _ => "Right"
    };

    private static string PathName(string lang, LinkChoice path) => path switch
    {
        LinkChoice.Lan => Catalog.Get(lang, "pathLan"),
        LinkChoice.Direct => Catalog.Get(lang, "pathP2p"),
        LinkChoice.Relay => Catalog.Get(lang, "pathRelay"),
        LinkChoice.Pending => Catalog.Get(lang, "waiting"),
        _ => Catalog.Get(lang, "pathOffline")
    };

    private void NotifyMany(params string[] names)
    {
        foreach (var name in names)
            Notify(name);
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

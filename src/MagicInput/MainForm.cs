using System.Diagnostics;
using System.Runtime.InteropServices;
using MagicInput.Models;
using MagicInput.Services;

namespace MagicInput;

public sealed class MainForm : Form
{
    private readonly bool _startInTray;
    private readonly string _initialPage;
    private readonly NotifyIcon _trayIcon;
    private readonly Dictionary<string, Panel> _pages = new();
    private readonly AppSettings _appSettings;
    private readonly KeyboardRemapper _keyboardRemapper = new();
    private readonly ThreeFingerDragService _threeFingerDragService = new();

    private bool _exitRequested;
    private DeviceReport _deviceReport = new();

    private Label _heroStatus = null!;
    private Label _driverStatus = null!;
    private Label _touchpadStatus = null!;
    private Label _keyboardStatus = null!;
    private Label _trackpadBatteryLabel = null!;
    private ProgressBar _trackpadBatteryBar = null!;
    private Label _keyboardBatteryLabel = null!;
    private ProgressBar _keyboardBatteryBar = null!;
    private Label _keyboardMapperStatus = null!;
    private TextBox _maintenanceOutput = null!;

    private RadioButton _hapticNatural = null!;
    private RadioButton _hapticDisabled = null!;
    private RadioButton _hapticMaximum = null!;
    private TrackBar _feedbackLevel = null!;
    private CheckBox _silentClicking = null!;
    private RadioButton _stopNormal = null!;
    private RadioButton _stopPressure = null!;
    private RadioButton _stopSize = null!;
    private NumericUpDown _stopPressureValue = null!;
    private NumericUpDown _stopSizeValue = null!;
    private CheckBox _ignoreButtonFinger = null!;
    private CheckBox _ignoreNearFingers = null!;
    private CheckBox _palmRejection = null!;
    private CheckBox _threeFingerDrag = null!;
    private ComboBox _bottomLeftTapAction = null!;
    private Label _threeFingerDragStatus = null!;
    private CheckBox _mediaRow = null!;
    private CheckBox _commandControlSwap = null!;
    private CheckBox _launchAtLogin = null!;

    public MainForm(bool startInTray, string initialPage = "Overview")
    {
        _startInTray = startInTray;
        _initialPage = initialPage;
        _appSettings = AppSettingsStore.Load();
        _trayIcon = BuildTrayIcon();

        Text = "Magic Input";
        MinimumSize = new Size(980, 680);
        Size = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.AppBackground;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Application;

        BuildUi();
        LoadTrackpadSettingsIntoUi();
        ApplyAppSettingsToUi();
        RefreshStatus();
        ShowPage(_initialPage);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startInTray)
        {
            BeginInvoke(() => Hide());
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyKeyboardMapperState();
        ApplyThreeFingerDragState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.Visible = true;
            return;
        }

        _keyboardRemapper.Dispose();
        _threeFingerDragService.Dispose();
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Palette.AppBackground
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var nav = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Palette.NavBackground,
            Padding = new Padding(18, 20, 18, 18)
        };
        root.Controls.Add(nav, 0, 0);

        var title = new Label
        {
            AutoSize = false,
            Height = 72,
            Dock = DockStyle.Top,
            Text = "Magic Input",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 15f),
            TextAlign = ContentAlignment.MiddleLeft,
            UseCompatibleTextRendering = false
        };
        nav.Controls.Add(title);

        var navFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Height = 310,
            Padding = new Padding(0, 16, 0, 0)
        };
        nav.Controls.Add(navFlow);
        navFlow.BringToFront();

        foreach (var page in new[] { "Overview", "Trackpad", "Keyboard", "Maintenance" })
        {
            var button = NavButton(page);
            button.Click += (_, _) => ShowPage(page);
            navFlow.Controls.Add(button);
        }

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            Text = "Clean-room utility\nSigned driver foundation",
            ForeColor = Color.FromArgb(188, 207, 207),
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.BottomLeft,
            UseCompatibleTextRendering = false
        };
        nav.Controls.Add(footer);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(26),
            BackColor = Palette.AppBackground
        };
        root.Controls.Add(content, 1, 0);

        _pages["Overview"] = BuildOverviewPage();
        _pages["Trackpad"] = BuildTrackpadPage();
        _pages["Keyboard"] = BuildKeyboardPage();
        _pages["Maintenance"] = BuildMaintenancePage();

        foreach (var page in _pages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            content.Controls.Add(page);
        }
    }

    private Panel BuildOverviewPage()
    {
        var page = Page("Overview", "Device health, driver state, and quick actions.");
        var body = Body(page);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 104,
            ForeColor = Palette.SubtleText,
            Text = "The trackpad driver is open-source and Microsoft-signed. This app owns the Windows control surface around it: status, settings, battery, startup, and keyboard media-row handling.",
            Padding = new Padding(0, 16, 0, 0),
            UseCompatibleTextRendering = false
        };
        body.Controls.Add(note);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, FlowDirection = FlowDirection.LeftToRight };
        body.Controls.Add(actions);
        actions.Controls.Add(ActionButton("Refresh", (_, _) => RefreshStatus()));
        actions.Controls.Add(ActionButton("Touchpad", (_, _) => OpenUri("ms-settings:devices-touchpad")));
        actions.Controls.Add(ActionButton("Bluetooth", (_, _) => OpenUri("ms-settings:bluetooth")));

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 360, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 12, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.Controls.Add(grid);

        _heroStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Palette.Text,
            Text = "Checking devices...",
            UseCompatibleTextRendering = false
        };
        body.Controls.Add(_heroStatus);

        _driverStatus = StatusCard(grid, 0, 0, "Driver", "Checking...", 2);
        _touchpadStatus = StatusCard(grid, 0, 1, "Trackpad", "Checking...");
        _keyboardStatus = StatusCard(grid, 1, 1, "Keyboard", "Checking...");

        return page;
    }

    private Panel BuildTrackpadPage()
    {
        var page = Page("Trackpad", "Haptics, palm rejection, click behavior, battery, and Windows gesture settings.");
        var body = Body(page);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.Controls.Add(split);

        var settings = Card("Click and haptics");
        split.Controls.Add(settings, 0, 0);

        _hapticNatural = Radio("Natural");
        _hapticDisabled = Radio("Disabled");
        _hapticMaximum = Radio("Maximum");
        _feedbackLevel = new TrackBar { Minimum = 0, Maximum = 2, TickStyle = TickStyle.BottomRight, Value = 1, Width = 230 };
        _silentClicking = Check("Silent clicking");
        _stopNormal = Radio("Leave click stopping normal");
        _stopPressure = Radio("Stop click by pressure");
        _stopSize = Radio("Stop click by contact size");
        _stopPressureValue = new NumericUpDown { Minimum = 0, Maximum = 100000, Width = 90 };
        _stopSizeValue = new NumericUpDown { Minimum = 0, Maximum = 100000, Width = 90, Value = 1 };
        _ignoreButtonFinger = Check("Ignore button finger during gestures");
        _ignoreNearFingers = Check("Ignore near-field fingers");
        _palmRejection = Check("Palm rejection");
        _threeFingerDrag = Check("Three-finger drag");
        _threeFingerDrag.CheckedChanged += (_, _) =>
        {
            _appSettings.ThreeFingerDragEnabled = _threeFingerDrag.Checked;
            AppSettingsStore.Save(_appSettings);
            ApplyThreeFingerDragState();
        };
        _bottomLeftTapAction = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 280,
            Height = 34,
            ForeColor = Palette.Text
        };
        _bottomLeftTapAction.Items.AddRange(CornerTapActionOptions().Cast<object>().ToArray());
        _bottomLeftTapAction.SelectedIndexChanged += (_, _) =>
        {
            _appSettings.BottomLeftTapAction = SelectedBottomLeftTapAction();
            AppSettingsStore.Save(_appSettings);
            ApplyThreeFingerDragState();
        };
        _threeFingerDragStatus = LabelText("Three-finger drag is off.");

        settings.Controls.Add(Stack(
            _hapticNatural,
            _hapticDisabled,
            _hapticMaximum,
            LabelText("Feedback level"),
            _feedbackLevel,
            _silentClicking,
            Separator(),
            _stopNormal,
            Inline(_stopPressure, _stopPressureValue),
            Inline(_stopSize, _stopSizeValue),
            Separator(),
            _ignoreButtonFinger,
            _ignoreNearFingers,
            _palmRejection,
            Separator(),
            _threeFingerDrag,
            LabelText("Bottom-left tap"),
            _bottomLeftTapAction,
            _threeFingerDragStatus,
            ActionButton("Apply trackpad settings", (_, _) => ApplyTrackpadSettings())));

        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        split.Controls.Add(right, 1, 0);

        var battery = Card("Battery");
        battery.Width = 360;
        _trackpadBatteryLabel = LabelText("Battery level unavailable until refreshed.");
        _trackpadBatteryBar = new ProgressBar { Width = 300, Height = 22, Minimum = 0, Maximum = 100 };
        battery.Controls.Add(Stack(_trackpadBatteryLabel, _trackpadBatteryBar, ActionButton("Refresh battery", (_, _) => RefreshTrackpadBattery())));
        right.Controls.Add(battery);

        var quick = Card("Quick actions");
        quick.Width = 360;
        quick.Controls.Add(Stack(
            ActionButton("Open Windows gestures", (_, _) => OpenUri("ms-settings:devices-touchpad")),
            ActionButton("Open driver control panel", (_, _) => OpenDriverControlPanel()),
            ActionButton("Refresh device state", (_, _) => RefreshStatus())));
        right.Controls.Add(quick);

        return page;
    }

    private Panel BuildKeyboardPage()
    {
        var page = Page("Keyboard", "Magic Keyboard detection, battery, Apple function row, and modifier mapping.");
        var body = Body(page);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Palette.AppBackground,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 320));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        body.Controls.Add(layout);

        var settings = Card("Function row");
        settings.Dock = DockStyle.Fill;
        layout.Controls.Add(settings, 0, 0);

        _mediaRow = Check("Apple function row");
        _mediaRow.CheckedChanged += (_, _) =>
        {
            _appSettings.MediaRowMapperEnabled = _mediaRow.Checked;
            AppSettingsStore.Save(_appSettings);
            ApplyKeyboardMapperState();
        };

        _keyboardMapperStatus = LabelText("Mapper off.");

        settings.Controls.Add(Stack(
            _mediaRow,
            _keyboardMapperStatus));

        var modifiers = Card("Modifier keys");
        modifiers.Dock = DockStyle.Fill;
        layout.Controls.Add(modifiers, 0, 1);

        _commandControlSwap = Check("Swap Cmd/Ctrl");
        _commandControlSwap.CheckedChanged += (_, _) =>
        {
            _appSettings.SwapCommandControlEnabled = _commandControlSwap.Checked;
            AppSettingsStore.Save(_appSettings);
            ApplyKeyboardMapperState();
        };

        modifiers.Controls.Add(Stack(
            _commandControlSwap,
            ActionButton("Settings", (_, _) => OpenUri("ms-settings:keyboard"))));

        var battery = Card("Battery");
        battery.Dock = DockStyle.Fill;
        layout.Controls.Add(battery, 0, 2);

        _keyboardBatteryLabel = LabelText("Refresh to read battery.");
        _keyboardBatteryBar = new ProgressBar { Width = 300, Height = 22, Minimum = 0, Maximum = 100 };
        battery.Controls.Add(Stack(
            _keyboardBatteryLabel,
            _keyboardBatteryBar,
            ActionButton("Refresh battery", (_, _) => RefreshKeyboardBattery())));

        var startup = Card("Startup");
        startup.Dock = DockStyle.Fill;
        layout.Controls.Add(startup, 0, 3);

        _launchAtLogin = Check("Start with Windows");
        _launchAtLogin.CheckedChanged += (_, _) =>
        {
            _appSettings.LaunchAtLogin = _launchAtLogin.Checked;
            AppSettingsStore.Save(_appSettings);
        };

        startup.Controls.Add(Stack(_launchAtLogin));

        return page;
    }

    private Panel BuildMaintenancePage()
    {
        var page = Page("Maintenance", "Driver management, verification, logs, and rollback.");
        var body = Body(page);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 104, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        body.Controls.Add(actions);
        actions.Controls.Add(ActionButton("Verify driver", (_, _) => RunScript("verify-trackpad-driver.ps1", false)));
        actions.Controls.Add(ActionButton("Install driver", (_, _) => RunScript("install-trackpad-driver.ps1", true)));
        actions.Controls.Add(ActionButton("Uninstall dry run", (_, _) => RunScript("uninstall-trackpad-driver.ps1", false)));
        actions.Controls.Add(ActionButton("Uninstall driver", (_, _) => RunScript("uninstall-trackpad-driver.ps1 -Execute", true)));
        actions.Controls.Add(ActionButton("Open workspace", (_, _) => OpenWorkspace()));

        _maintenanceOutput = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            ReadOnly = true,
            Font = new Font("Cascadia Mono", 9f),
            BackColor = Color.White,
            ForeColor = Palette.Text
        };
        body.Controls.Add(_maintenanceOutput);
        _maintenanceOutput.BringToFront();

        return page;
    }

    private void LoadTrackpadSettingsIntoUi()
    {
        var settings = TrackpadSettingsStore.Read();
        _hapticNatural.Checked = settings.HapticMode == HapticMode.Natural;
        _hapticDisabled.Checked = settings.HapticMode == HapticMode.Disabled;
        _hapticMaximum.Checked = settings.HapticMode == HapticMode.Maximum;
        _feedbackLevel.Value = Math.Clamp(settings.FeedbackLevel, 0, 2);
        _silentClicking.Checked = settings.SilentClicking;
        _stopNormal.Checked = settings.StopMode == ClickStopMode.Normal;
        _stopPressure.Checked = settings.StopMode == ClickStopMode.Pressure;
        _stopSize.Checked = settings.StopMode == ClickStopMode.ContactSize;
        _stopPressureValue.Value = Math.Clamp(settings.StopPressure, 0, 100000);
        _stopSizeValue.Value = Math.Clamp(settings.StopSize, 0, 100000);
        _ignoreButtonFinger.Checked = settings.IgnoreButtonFinger;
        _ignoreNearFingers.Checked = settings.IgnoreNearFingers;
        _palmRejection.Checked = settings.PalmRejection;
    }

    private void ApplyAppSettingsToUi()
    {
        _mediaRow.Checked = _appSettings.MediaRowMapperEnabled;
        _commandControlSwap.Checked = _appSettings.SwapCommandControlEnabled;
        _threeFingerDrag.Checked = _appSettings.ThreeFingerDragEnabled;
        SelectBottomLeftTapAction(_appSettings.BottomLeftTapAction);
        _launchAtLogin.Checked = _appSettings.LaunchAtLogin;
        ApplyKeyboardMapperState();
        ApplyThreeFingerDragState();
    }

    private void ApplyTrackpadSettings()
    {
        var settings = new TrackpadSettings
        {
            HapticMode = _hapticDisabled.Checked ? HapticMode.Disabled : _hapticMaximum.Checked ? HapticMode.Maximum : HapticMode.Natural,
            FeedbackLevel = _feedbackLevel.Value,
            SilentClicking = _silentClicking.Checked,
            StopMode = _stopPressure.Checked ? ClickStopMode.Pressure : _stopSize.Checked ? ClickStopMode.ContactSize : ClickStopMode.Normal,
            StopPressure = (int)_stopPressureValue.Value,
            StopSize = (int)_stopSizeValue.Value,
            IgnoreButtonFinger = _ignoreButtonFinger.Checked,
            IgnoreNearFingers = _ignoreNearFingers.Checked,
            PalmRejection = _palmRejection.Checked
        };

        try
        {
            var result = TrackpadSettingsStore.ApplyWithElevationIfNeeded(settings);
            MessageBox.Show(result.Message, result.Ok ? "Magic Input" : "Apply failed", MessageBoxButtons.OK,
                result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Apply failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshStatus()
    {
        try
        {
            _deviceReport = DeviceInventory.Read();
            _heroStatus.Text = _deviceReport.TrackpadFilterStarted && _deviceReport.PrecisionTouchpadPresent
                ? "Magic Trackpad is running as a Precision Touchpad."
                : "Trackpad driver needs attention.";

            _driverStatus.Text = _deviceReport.DriverInstalled
                ? "Installed\nMicrosoft-signed"
                : "Not installed";

            _touchpadStatus.Text = _deviceReport.PrecisionTouchpadPresent
                ? "Precision Touchpad active\nFilter started"
                : _deviceReport.TrackpadBluetoothPresent
                    ? "Paired, but precision layer missing"
                    : "Not detected";

            _keyboardStatus.Text = _deviceReport.KeyboardPresent
                ? "Detected\nMedia row ready"
                : "Not detected";
        }
        catch (Exception ex)
        {
            _heroStatus.Text = "Could not refresh device state.";
            _maintenanceOutput.Text = ex.ToString();
        }
    }

    private void RefreshTrackpadBattery()
    {
        SetBatteryUi(TrackpadBatteryService.Read(true), _trackpadBatteryLabel, _trackpadBatteryBar);
    }

    private void RefreshKeyboardBattery()
    {
        SetBatteryUi(KeyboardBatteryReader.Read(), _keyboardBatteryLabel, _keyboardBatteryBar);
    }

    private static void SetBatteryUi(BatteryReadResult result, Label label, ProgressBar bar)
    {
        if (result.Ok)
        {
            bar.Value = Math.Clamp(result.Level, 0, 100);
            label.Text = $"Battery: {result.Level}%  Last updated {DateTime.Now:t}";
            return;
        }

        bar.Value = 0;
        label.Text = result.Message;
    }

    private static string ShortDriverVersion(string driverVersion)
    {
        if (string.IsNullOrWhiteSpace(driverVersion))
        {
            return "Driver version available";
        }

        var parts = driverVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[^1] : driverVersion;
    }

    private void RunScript(string scriptCommand, bool elevated)
    {
        var parts = scriptCommand.Split(' ', 2);
        var script = WorkspaceLocator.FindScript(parts[0]);
        if (script == null)
        {
            MessageBox.Show("Could not find script in the workspace.", "Magic Input", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var arguments = parts.Length > 1 ? parts[1] : "";
        var psArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {arguments}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = psArgs,
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated,
            CreateNoWindow = !elevated,
            WindowStyle = elevated ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return;
            }

            if (!elevated)
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                _maintenanceOutput.Text = output + (string.IsNullOrWhiteSpace(error) ? "" : Environment.NewLine + error);
            }
            else
            {
                process.WaitForExit();
                _maintenanceOutput.Text = $"Elevated command finished with exit code {process.ExitCode}. Refreshing state...";
                RefreshStatus();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Script failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDriverControlPanel()
    {
        var controlPanel = WorkspaceLocator.FindControlPanel();
        if (controlPanel == null)
        {
            MessageBox.Show("The driver control panel executable was not found in the local package.", "Magic Input", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(controlPanel) { UseShellExecute = true });
    }

    private void OpenWorkspace()
    {
        var root = WorkspaceLocator.FindRoot();
        if (root != null)
        {
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
        }
    }

    private static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void ShowPage(string name)
    {
        foreach (var pair in _pages)
        {
            pair.Value.Visible = pair.Key == name;
        }
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Magic Input", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Refresh devices", null, (_, _) => RefreshStatus());
        menu.Items.Add("Touchpad settings", null, (_, _) => OpenUri("ms-settings:devices-touchpad"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _exitRequested = true; Close(); });

        var tray = new NotifyIcon
        {
            Text = "Magic Input",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        return tray;
    }

    private void ApplyKeyboardMapperState()
    {
        _keyboardRemapper.Configure(_appSettings.MediaRowMapperEnabled, _appSettings.SwapCommandControlEnabled);
        if (_keyboardMapperStatus != null)
        {
            _keyboardMapperStatus.Text = _keyboardRemapper.IsActive ? "Mapper active." : _keyboardRemapper.Status;
        }
    }

    private void ApplyThreeFingerDragState()
    {
        _threeFingerDragService.Configure(_appSettings.ThreeFingerDragEnabled, _appSettings.BottomLeftTapAction);
        if (_threeFingerDragStatus != null)
        {
            _threeFingerDragStatus.Text = _threeFingerDragService.Status;
        }
    }

    private CornerTapAction SelectedBottomLeftTapAction()
    {
        return _bottomLeftTapAction.SelectedItem is CornerTapActionOption option
            ? option.Action
            : CornerTapAction.Off;
    }

    private void SelectBottomLeftTapAction(CornerTapAction action)
    {
        for (var index = 0; index < _bottomLeftTapAction.Items.Count; index++)
        {
            if (_bottomLeftTapAction.Items[index] is CornerTapActionOption option && option.Action == action)
            {
                _bottomLeftTapAction.SelectedIndex = index;
                return;
            }
        }

        _bottomLeftTapAction.SelectedIndex = 0;
    }

    private static IEnumerable<CornerTapActionOption> CornerTapActionOptions()
    {
        yield return new CornerTapActionOption("Off", CornerTapAction.Off);
        yield return new CornerTapActionOption("Clipboard history (Win+V)", CornerTapAction.ClipboardHistory);
        yield return new CornerTapActionOption("Start menu", CornerTapAction.StartMenu);
        yield return new CornerTapActionOption("Task View", CornerTapAction.TaskView);
        yield return new CornerTapActionOption("Show Desktop", CornerTapAction.ShowDesktop);
        yield return new CornerTapActionOption("Previous window", CornerTapAction.PreviousWindow);
    }

    private static Button NavButton(string text) => new()
    {
        Text = text,
        Width = 276,
        Height = 58,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.White,
        BackColor = Palette.NavButton,
        Font = new Font("Segoe UI Semibold", 10f),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(16, 0, 0, 0),
        UseCompatibleTextRendering = false,
        Margin = new Padding(0, 0, 0, 12)
    };

    private static Panel Page(string title, string subtitle)
    {
        var page = new TableLayoutPanel
        {
            BackColor = Palette.AppBackground,
            ColumnCount = 1,
            RowCount = 2
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var body = new Panel
        {
            Name = "Body",
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Palette.AppBackground
        };
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 86,
            BackColor = Palette.AppBackground
        };
        var titleLabel = new Label
        {
            Location = new Point(0, 0),
            Size = new Size(900, 76),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = title,
            Font = new Font("Segoe UI Semibold", 19f),
            ForeColor = Palette.Text,
            AutoEllipsis = true,
            UseCompatibleTextRendering = false
        };
        var subtitleLabel = new Label
        {
            Location = new Point(2, 82),
            Size = new Size(900, 42),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = subtitle,
            Font = new Font("Segoe UI", 10f),
            ForeColor = Palette.SubtleText,
            AutoEllipsis = true,
            UseCompatibleTextRendering = false
        };
        header.Controls.Add(titleLabel);
        header.Controls.Add(subtitleLabel);
        page.Controls.Add(header, 0, 0);
        page.Controls.Add(body, 0, 1);
        titleLabel.BringToFront();
        return page;
    }

    private static Panel Body(Panel page)
    {
        return (Panel)page.Controls["Body"]!;
    }

    private static Panel Card(string title)
    {
        var card = new Panel
        {
            BackColor = Color.White,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 18, 18),
            Width = 500,
            Height = 520
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        var label = new Label
        {
            Name = "CardTitle",
            Text = title,
            Dock = DockStyle.Top,
            Height = 62,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI Semibold", 11f),
            UseCompatibleTextRendering = false
        };
        card.Controls.Add(label);
        label.BringToFront();
        return card;
    }

    private static Label StatusCard(TableLayoutPanel grid, int column, int row, string title, string value, int columnSpan = 1)
    {
        var card = new Panel
        {
            BackColor = Color.White,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 18, 18)
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        card.Dock = DockStyle.Fill;

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White
        };
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = title,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var label = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 8.75f),
            TextAlign = ContentAlignment.TopLeft
        };

        inner.Controls.Add(titleLabel, 0, 0);
        inner.Controls.Add(label, 0, 1);
        card.Controls.Add(inner);

        grid.Controls.Add(card, column, row);
        if (columnSpan > 1)
        {
            grid.SetColumnSpan(card, columnSpan);
        }
        return label;
    }

    private static Panel Stack(params Control[] controls)
    {
        var panel = new VerticalScrollPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0)
        };

        const int maxContentWidth = 420;
        var y = 62;
        foreach (var control in controls)
        {
            var preferredHeight = Math.Max(control.Height, control.PreferredSize.Height);
            if (control is RadioButton or CheckBox)
            {
                control.AutoSize = false;
                control.Width = maxContentWidth;
                control.Height = Math.Max(40, preferredHeight + 8);
            }
            else if (control is FlowLayoutPanel)
            {
                control.Width = maxContentWidth;
                control.Height = Math.Max(42, preferredHeight);
            }
            else if (control is not TrackBar)
            {
                control.Width = Math.Min(control.Width, maxContentWidth);
                control.Height = Math.Max(control.Height, preferredHeight);
            }

            control.Location = new Point(0, y);
            panel.Controls.Add(control);
            y += control.Height + 12;
        }

        panel.AutoScrollMinSize = new Size(0, y + 12);
        panel.Resize += (_, _) => FitStackChildWidths(panel, maxContentWidth);
        panel.HandleCreated += (_, _) => FitStackChildWidths(panel, maxContentWidth);
        return panel;
    }

    private static void FitStackChildWidths(Panel panel, int maxContentWidth)
    {
        var contentWidth = Math.Max(220, Math.Min(maxContentWidth, panel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10));
        foreach (Control child in panel.Controls)
        {
            if (child is NumericUpDown)
            {
                continue;
            }

            child.Width = child is TrackBar ? Math.Min(230, contentWidth) : contentWidth;
        }
    }

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 10, 0);
            flow.Controls.Add(control);
        }

        return flow;
    }

    private static Label LabelText(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 280,
        Height = 52,
        ForeColor = Palette.SubtleText,
        UseCompatibleTextRendering = false,
        AutoEllipsis = false
    };

    private static RadioButton Radio(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 34),
        ForeColor = Palette.Text,
        UseCompatibleTextRendering = false
    };

    private static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 34),
        ForeColor = Palette.Text,
        UseCompatibleTextRendering = false
    };

    private static Button ActionButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Width = 250,
            Height = 56,
            BackColor = Palette.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = false,
            Margin = new Padding(0, 0, 10, 12)
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += handler;
        return button;
    }

    private static Panel Separator() => new()
    {
        Height = 1,
        Width = 280,
        BackColor = Palette.Border,
        Margin = new Padding(0, 8, 0, 14)
    };

    private sealed class CornerTapActionOption
    {
        public CornerTapActionOption(string label, CornerTapAction action)
        {
            Label = label;
            Action = action;
        }

        public string Label { get; }
        public CornerTapAction Action { get; }

        public override string ToString() => Label;
    }

    private sealed class VerticalScrollPanel : Panel
    {
        private const int SbHorz = 0;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            HideHorizontalScrollBar();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            HideHorizontalScrollBar();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            HideHorizontalScrollBar();
        }

        private void HideHorizontalScrollBar()
        {
            if (IsHandleCreated)
            {
                ShowScrollBar(Handle, SbHorz, false);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
    }

    private static class Palette
    {
        public static readonly Color AppBackground = Color.FromArgb(246, 248, 250);
        public static readonly Color NavBackground = Color.FromArgb(20, 50, 54);
        public static readonly Color NavButton = Color.FromArgb(31, 78, 82);
        public static readonly Color Accent = Color.FromArgb(15, 118, 110);
        public static readonly Color Border = Color.FromArgb(214, 222, 229);
        public static readonly Color Text = Color.FromArgb(24, 32, 38);
        public static readonly Color SubtleText = Color.FromArgb(91, 105, 119);
    }
}

using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DosboxEnhanced;

public sealed partial class MainWindow : Window
{
    readonly Library library = Library.Load();
    readonly ApplicationPreferences preferences = ApplicationPreferences.Load();
    readonly Grid root = new(), deck = new();
    readonly WrapPanel menu = new();
    readonly Border stage = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    readonly Image image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly NativeView nativeView = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
    readonly Popup fullscreenMenu = new() { AllowsTransparency = false, StaysOpen = true, Placement = PlacementMode.Absolute };
    readonly TextBlock welcome = new() { Text = "Your DOS workspace\n\nOpen a DOS program for a quick run,\nor create an environment with your own drives.", FontSize = 22, Foreground = new SolidColorBrush(Color.FromRgb(110, 133, 159)), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { Margin = new Thickness(12, 6, 12, 6), Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap };
    readonly ListBox environments = new(), drives = new(), files = new();
    readonly TextBox name = new(), startup = new(), cycles = new() { Text = "auto" };
    readonly ComboBox memory = new(), browseDrive = new(), scaler = new(), aspectMode = new();
    readonly CheckBox enabled = new() { Content = "Enable neural rendering", IsChecked = true }, compare = new() { Content = "Before / after split" };
    readonly ComboBox renderer = new();
    readonly ComboBox upscaleOrder = new(), upscaleFactor = new();
    readonly CheckBox inputResize = new() { Content = "Resize input", ToolTip = "Optional resize before NR and NVIDIA SR. Off keeps the game's native pixels." };
    readonly ComboBox inputFilter = new();
    readonly Slider inputScale = new() { Minimum = .25, Maximum = 4, Value = 2, TickFrequency = .05, IsSnapToTickEnabled = true, SmallChange = .05, LargeChange = .25, IsMoveToPointEnabled = true };
    readonly TextBlock inputScaleLabel = new() { Foreground = Brush(221, 231, 241), VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Right };
    readonly TextBlock inputResolution = new() { Foreground = Brush(157, 180, 204), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    readonly StackPanel inputResizeOptions = new() { Visibility = Visibility.Collapsed };
    readonly CheckBox nvidiaUpscaling = new() { Content = "NVIDIA Super Resolution", IsChecked = true }, nvidiaFrameGeneration = new() { Content = "NVIDIA Frame Generation", IsChecked = true };
    readonly TextBlock nvidiaStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    readonly Slider volume = new() { Minimum = 0, Maximum = 100, Width = 100, TickFrequency = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox muted = new() { Content = "Mute", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock volumeLabel = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
    readonly DispatcherTimer preferencesTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Slider intensity = new() { Minimum = 0, Maximum = 2, Value = .65 }, tone = new() { Minimum = 0, Maximum = 2, Value = 1 }, structure = new() { Minimum = 0, Maximum = 2, Value = 1 };
    readonly ComboBox style = new(), passes = new();
    readonly TextBlock phoenixStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    readonly TextBlock browserPath = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush(221, 231, 241) };
    readonly TopEdgeDwell dwell = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    DosCore? core;
    PhoenixProcessor phoenix = new();
    EnvironmentProfile? selected, running;
    WriteableBitmap? bitmap;
    VideoFrame? displayed;
    long submitted = -1, shown = -1, enhancedSeen = -1;
    double processedAt, hideSince;
    bool fullscreen, panels, changing, disposed;
    string currentDirectory = "", driveRoot = "", runningConfig = "";
    WindowState previousState;
    Point? previousMouse;
    public DosCore? Core => core;
    public PhoenixProcessor Phoenix => phoenix;
    public string CurrentStatus => status.Text;
    public bool BackgroundAutomation { get; }
    public MainWindow(string? executable = null, bool backgroundAutomation = false)
    {
        BackgroundAutomation = backgroundAutomation;
        if (BackgroundAutomation) { ShowActivated = false; ShowInTaskbar = false; WindowState = WindowState.Minimized; }
        Title = "DOSBox Enhanced · 1.0 Beta"; Width = 1440; Height = 920; MinWidth = 1050; MinHeight = 640;
        Background = Brush(15, 20, 29); Foreground = Brush(221, 231, 241); FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        environmentVisible = preferences.EnvironmentVisible; effectsVisible = preferences.EffectsVisible;
        root.Background = Background; ConfigureTheme(); BuildUi(); InitializePrograms(); InitializeMouseCapture(); ApplyLayout();
        InitializeMcp();
        menu.Resources = Resources;
        menu.Background = Brush(23, 31, 42);
        TextElement.SetForeground(menu, Foreground);
        nativeView.WindowReady += hwnd => phoenix.AttachWindow(hwnd);
        nativeView.NativeInput += NativeGameInput;
        Loaded += (_, _) => { if (nativeView.Surface != IntPtr.Zero) phoenix.AttachWindow(nativeView.Surface); };
        volume.Value = Math.Clamp(preferences.Audio.Volume, 0, 100); muted.IsChecked = preferences.Audio.Muted;
        UpdateAudio(false);
        preferencesTimer.Tick += (_, _) => { preferencesTimer.Stop(); preferences.Save(); };
        volume.ValueChanged += (_, _) => UpdateAudio(); muted.Checked += (_, _) => UpdateAudio(); muted.Unchecked += (_, _) => UpdateAudio();
        environments.ItemsSource = library.Environments;
        environments.SelectionChanged += (_, _) => SelectEnvironment(environments.SelectedItem as EnvironmentProfile);
        if (library.Environments.Count == 0) NewEnvironment();
        if (library.Environments.Count > 0) environments.SelectedIndex = 0;
        timer.Tick += (_, _) => Tick(); timer.Start();
        stage.KeyDown += (_, e) => GameKey(e, true); stage.KeyUp += (_, e) => GameKey(e, false);
        stage.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(stage);
            if (!mouseCaptured && !preferences.Mouse.CaptureOnClick && core != null && previousMouse is Point old && stage.IsKeyboardFocusWithin)
            {
                var frame = core.Latest;
                core.MouseMove((int)Math.Round((p.X - old.X) * (frame?.Width ?? 640) / Math.Max(1, image.ActualWidth)), (int)Math.Round((p.Y - old.Y) * (frame?.Height ?? 400) / Math.Max(1, image.ActualHeight)));
            }
            previousMouse = p;
        };
        stage.MouseLeave += (_, _) => { if (!mouseCaptured) { previousMouse = null; core?.ReleaseKeys(); } };
        stage.MouseDown += (_, e) => { stage.Focus(); if (!mouseCaptured && preferences.Mouse.CaptureOnClick) Try(() => CaptureGameMouse()); else core?.MouseButton(e.ChangedButton == MouseButton.Left ? 1 : e.ChangedButton == MouseButton.Middle ? 4 : 2, true); e.Handled = true; };
        stage.MouseUp += (_, e) => { if (mouseCaptured || !preferences.Mouse.CaptureOnClick) core?.MouseButton(e.ChangedButton == MouseButton.Left ? 1 : e.ChangedButton == MouseButton.Middle ? 4 : 2, false); e.Handled = true; };
        stage.MouseWheel += (_, e) => { if (mouseCaptured || !preferences.Mouse.CaptureOnClick) core?.MouseWheel(Math.Sign(e.Delta)); e.Handled = true; };
        stage.LostKeyboardFocus += (_, _) => { ReleaseMouseCapture(); core?.ReleaseKeys(); };
        Deactivated += (_, _) => { programPicker.Close(); ReleaseMouseCapture(); core?.ReleaseKeys(); previousMouse = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (HandleMouseReleaseKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, true)) { e.Handled = true; return; }
            if (e.Key == Key.F11 || (e.Key == Key.System && e.SystemKey == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))) { ToggleFullscreen(); e.Handled = true; }
            if (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ShowMenu(true); SetAllPanels(!panels); core?.ReleaseKeys(); e.Handled = true; }
        };
        PreviewKeyUp += (_, e) => { if (HandleMouseReleaseKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, false)) e.Handled = true; };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) Try(() => QuickRun(paths[0])); };
        Closing += (_, _) => DisposeApp();
        SizeChanged += (_, _) => { ReleaseMouseCapture(); FitImage(); };
        stage.SizeChanged += (_, _) => FitImage();
        if (executable != null) Loaded += (_, _) => Try(() => QuickRun(executable));
    }
    static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    void ConfigureTheme()
    {
        var control = new Style(typeof(Control));
        control.Setters.Add(new Setter(Control.ForegroundProperty, Brush(221, 231, 241)));
        control.Setters.Add(new Setter(Control.BackgroundProperty, Brush(30, 40, 54)));
        control.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(57, 72, 91)));
        Resources[typeof(Control)] = control;
        foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(ListBox), typeof(ComboBox), typeof(TabControl), typeof(TabItem), typeof(CheckBox), typeof(Slider) })
        {
            var s = new Style(type, control);
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3)));
            Resources[type] = s;
        }
        var comboItem = new Style(typeof(ComboBoxItem));
        comboItem.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black)); Resources[typeof(ComboBoxItem)] = comboItem;
        // The stock ComboBox/TabItem templates use a light selected surface;
        // retain readable text rather than inheriting the workspace foreground.
        ((Style)Resources[typeof(ComboBox)]).Setters.Add(new Setter(Control.ForegroundProperty, Brush(25, 35, 45)));
        var tab = (Style)Resources[typeof(TabItem)];
        var selectedTab = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selectedTab.Setters.Add(new Setter(Control.ForegroundProperty, Brush(25, 35, 45))); tab.Triggers.Add(selectedTab);
        ConfigureCheckboxes();
    }
    static TextBlock Label(string text, int size = 13) => new() { Text = text, Foreground = Brush(221, 231, 241), FontSize = size, Margin = new Thickness(0, 9, 0, 3), TextWrapping = TextWrapping.Wrap };
    Button Button(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(11, 7, 11, 7) };
        b.Click += (_, _) => { ReleaseMouseCapture(); Try(action); }; return b;
    }
    void Try(Action action) { try { action(); } catch (Exception e) { Notice(e.Message); } }
    public void Notice(string message)
    {
        if (BackgroundAutomation) { File.AppendAllText(Path.Combine(Paths.Data, "errors.log"), DateTimeOffset.Now + " " + message + "\n"); return; }
        MessageBox.Show(this, message, "DOSBox Enhanced", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    void BuildUi()
    {
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        menu.Background = Brush(22, 30, 42); menu.Margin = new Thickness(0, 0, 0, 4);
        BuildToolbar();
        InitializeUpdateNotice();
        Grid.SetZIndex(menu, 2); root.Children.Add(menu);
        deck.ColumnDefinitions.Add(new() { Width = new GridLength(285) }); deck.ColumnDefinitions.Add(new()); deck.ColumnDefinitions.Add(new() { Width = new GridLength(270) });
        Grid.SetRow(deck, 1); root.Children.Add(deck);
        var left = new TabControl { Margin = new Thickness(8, 0, 8, 0) };
        var envPanel = new StackPanel();
        envPanel.Children.Add(Label("Saved environments", 18)); environments.Height = 200; envPanel.Children.Add(environments);
        var envButtons = new WrapPanel(); envButtons.Children.Add(Button("New", NewEnvironment)); envButtons.Children.Add(Button("Copy environment", Duplicate)); envButtons.Children.Add(Button("Remove", Remove)); envPanel.Children.Add(envButtons);
        envPanel.Children.Add(Label("Name")); envPanel.Children.Add(name); envPanel.Children.Add(Label("Startup program (blank = DOS prompt)")); envPanel.Children.Add(startup);
        envPanel.Children.Add(Label("CPU cycles")); envPanel.Children.Add(CpuControls()); envPanel.Children.Add(Label("Memory (MB)"));
        foreach (int value in Enumerable.Range(1, 64).Concat([96, 128, 224, 256, 512, 1024])) memory.Items.Add(value); memory.SelectedItem = 32; envPanel.Children.Add(memory);
        envPanel.Children.Add(Button("Save environment", SaveSelected));
        envPanel.Children.Add(Button("DOSBox configuration…", ShowDosboxSettings));
        envPanel.Children.Add(Button("Manage program names & menu…", ShowProgramManager));
        envPanel.Children.Add(Label("Profiles save drives, startup, CPU, memory and effects. Save state also captures the running DOS machine."));
        left.Items.Add(new TabItem { Header = "Environments", Content = new ScrollViewer { Content = envPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var drivePanel = new StackPanel(); drivePanel.Children.Add(Label("Mounted folders", 18)); drives.Height = 280; drivePanel.Children.Add(drives);
        drivePanel.Children.Add(Button("Add folder drive…", () => EditDrive(null))); drivePanel.Children.Add(Button("Edit selected drive…", () => { if (drives.SelectedItem is Drive drive) EditDrive(drive); }));
        drivePanel.Children.Add(Button("Remove drive", () => { if (selected != null && drives.SelectedItem is Drive drive) { selected.Drives.Remove(drive); SaveSelected(); RefreshBrowserDrives(); } }));
        drivePanel.Children.Add(Label("Use C: for programs and extra letters for data or CD folders. Drive changes apply on the next launch. Mounted folders are writable; normal DOS saves go into those folders."));
        left.Items.Add(new TabItem { Header = "Drives", Content = drivePanel });
        var browser = new DockPanel();
        var browserTop = new StackPanel(); browserTop.Children.Add(Label("Browse & run", 18)); browserTop.Children.Add(browseDrive); browserTop.Children.Add(browserPath);
        browserTop.Children.Add(Button("Parent folder", () => Browse(Directory.GetParent(currentDirectory)?.FullName ?? driveRoot)));
        browserTop.Children.Add(Button("New folder…", CreateFolder));
        browserTop.Children.Add(Button("Run selected game", RunSelectedGame));
        browseDrive.SelectionChanged += (_, _) => { if (browseDrive.SelectedItem is Drive drive) { driveRoot = Path.GetFullPath(drive.Folder); Browse(driveRoot); } };
        DockPanel.SetDock(browserTop, Dock.Top); browser.Children.Add(browserTop); browser.Children.Add(files);
        files.DisplayMemberPath = "Name";
        var fileItemStyle = new Style(typeof(ListBoxItem));
        fileItemStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("Name"))); files.ItemContainerStyle = fileItemStyle;
        files.MouseDoubleClick += (_, _) => Try(() =>
        {
            if (files.SelectedItem is BrowserEntry { Item: DirectoryInfo dir }) Browse(dir.FullName);
            else if (files.SelectedItem is BrowserEntry { Item: FileInfo file } && selected != null && browseDrive.SelectedItem is Drive drive)
            {
                selected.Startup = drive.Letter + ":\\" + Path.GetRelativePath(driveRoot, file.FullName); startup.Text = selected.Startup; LaunchSelected();
            }
        });
        left.Items.Add(new TabItem { Header = "Files", Content = browser }); deck.Children.Add(left);
        var view = new Grid(); view.Children.Add(welcome); view.Children.Add(image); view.Children.Add(nativeView); stage.Child = view; Grid.SetColumn(stage, 1); deck.Children.Add(stage);
        var right = new StackPanel { Margin = new Thickness(16, 8, 16, 8) }; right.Children.Add(Label("DLSS 5", 20));
        right.Children.Add(inputResize);
        inputScale.ToolTip = "0.25×–4× native resolution, in 0.05× steps. Release to apply. Arrow keys adjust; Home restores 0.25×, End selects 4×.";
        inputFilter.Items.Add("Sharp pixels"); inputFilter.Items.Add("Smooth"); inputFilter.SelectedIndex = 0;
        var inputRow = new Grid(); inputRow.ColumnDefinitions.Add(new()); inputRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); inputRow.Children.Add(inputScale); Grid.SetColumn(inputScaleLabel, 1); inputRow.Children.Add(inputScaleLabel);
        inputResizeOptions.Children.Add(inputRow); inputResizeOptions.Children.Add(inputResolution); inputResizeOptions.Children.Add(inputFilter); right.Children.Add(inputResizeOptions);
        right.Children.Add(Label("Neural renderer", 13));
        renderer.Items.Add("Phoenix DLSS5"); renderer.Items.Add("NVIDIA DLSS5"); renderer.SelectedIndex = 0; right.Children.Add(renderer);
        right.Children.Add(enabled); right.Children.Add(compare);
        AddQuickSlider(right,"Intensity",intensity); AddQuickSlider(right,"Local tone",tone); AddQuickSlider(right,"Structure",structure);
        right.Children.Add(Label("Style")); foreach (string s in new[] { "Default", "Warm", "Cool" }) style.Items.Add(s); style.SelectedIndex = 0; right.Children.Add(style);
        right.Children.Add(Label("Passes")); foreach (int p in new[] { 1, 2, 3, 4 }) passes.Items.Add(p); passes.SelectedIndex = 0; right.Children.Add(passes);
        right.Children.Add(phoenixStatus);
        right.Children.Add(Label("NVIDIA upscaling & frame generation", 16));
        right.Children.Add(nvidiaUpscaling);
        right.Children.Add(Label("Processing order")); upscaleOrder.Items.Add("NR first → upscale"); upscaleOrder.Items.Add("Upscale first → NR"); upscaleOrder.SelectedIndex = 0; right.Children.Add(upscaleOrder);
        right.Children.Add(Label("Upscale size")); foreach (int factor in new[] { 2, 3, 4 }) upscaleFactor.Items.Add(factor); upscaleFactor.SelectedItem = 4; right.Children.Add(upscaleFactor);
        upscaleFactor.ToolTip = "Multiply the game's native resolution. With upscaling off, processing stays at native resolution.";
        right.Children.Add(nvidiaFrameGeneration); right.Children.Add(nvidiaStatus);
        right.Children.Add(Label("Display", 18)); foreach (string s in new[] { "Smooth scaling", "Sharp pixels", "Integer scaling" }) scaler.Items.Add(s); scaler.SelectedIndex = 0; right.Children.Add(scaler);
        foreach (string s in new[] { "DOS corrected aspect", "Square pixels", "Fill window" }) aspectMode.Items.Add(s); aspectMode.SelectedIndex = 0; right.Children.Add(aspectMode);
        right.Children.Add(Label("Input", 18)); right.Children.Add(mouseHelp); right.Children.Add(Button("Mouse capture settings…", ShowMouseSettings));
        right.Children.Add(Label("Native rendering uses estimated motion from DOS images and a flat 2D plane. Super Resolution enlarges the image; Frame Generation adds frames. Generated-frame counts come from NVIDIA's presenter."));
        BuildPhoenixPanel(right);
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(rightScroll, 2); deck.Children.Add(rightScroll);
        Grid.SetRow(status, 2); root.Children.Add(status); Content = root;
        enabled.Checked += (_, _) => UpdateEffects(); enabled.Unchecked += (_, _) => UpdateEffects();
        compare.Checked += (_, _) => UpdateEffects(); compare.Unchecked += (_, _) => UpdateEffects();
        foreach (var slider in new[] { intensity, tone, structure }) slider.ValueChanged += (_, _) => UpdateEffects();
        style.SelectionChanged += (_, _) => UpdateEffects(); passes.SelectionChanged += (_, _) => UpdateEffects();
        renderer.SelectionChanged += (_, _) => UpdateEffects();
        upscaleOrder.SelectionChanged += (_, _) => UpdateEffects(); upscaleFactor.SelectionChanged += (_, _) => UpdateEffects();
        inputResize.Checked += (_, _) => UpdateEffects(); inputResize.Unchecked += (_, _) => UpdateEffects();
        inputScale.ValueChanged += (_, _) => { UpdateInputPreview(); if (!inputScale.IsMouseCaptureWithin) UpdateEffects(); };
        inputScale.LostMouseCapture += (_, _) => UpdateEffects(); inputFilter.SelectionChanged += (_, _) => UpdateEffects();
        nvidiaUpscaling.Checked += (_, _) => UpdateEffects(); nvidiaUpscaling.Unchecked += (_, _) => UpdateEffects();
        nvidiaFrameGeneration.Checked += (_, _) => UpdateEffects(); nvidiaFrameGeneration.Unchecked += (_, _) => UpdateEffects();
        scaler.SelectionChanged += (_, _) => { phoenix.SharpPixels = scaler.SelectedIndex != 0; submitted = -1; FitImage(); }; aspectMode.SelectionChanged += (_, _) => FitImage();
    }
    void UpdateEffects()
    {
        if (changing) return;
        RefreshStyleChoices();
        phoenix.Settings = new(enabled.IsChecked == true, intensity.Value, tone.Value, structure.Value, Math.Max(0, style.SelectedIndex), compare.IsChecked == true, passes.SelectedItem is int n ? n : 1,
            renderer.SelectedIndex == 1 ? "nvidia" : "phoenix", nvidiaUpscaling.IsChecked == true, nvidiaFrameGeneration.IsChecked == true,
            upscaleOrder.SelectedIndex == 1 ? "before_nr" : "after_nr", upscaleFactor.SelectedItem is int factor ? factor : 4,
            inputResize.IsChecked == true, inputScale.Value, inputFilter.SelectedIndex == 1 ? "linear" : "nearest") { Phoenix = advancedSettings };
        phoenixAdvanced.IsEnabled = renderer.SelectedIndex != 1;
        inputResizeOptions.Visibility = inputResize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateInputPreview();
        upscaleOrder.IsEnabled = upscaleFactor.IsEnabled = nvidiaUpscaling.IsChecked == true;
        passes.IsEnabled = renderer.SelectedIndex != 1;
        passes.ToolTip = passes.IsEnabled ? "Repeat Phoenix DLSS5 processing for a stronger effect" : "NVIDIA DLSS5 neural rendering uses one pass";
        if (selected != null) { selected.Effects = phoenix.Settings; library.Save(); }
        phoenix.Reset(); submitted = shown = enhancedSeen = -1;
    }
    void UpdateInputPreview()
    {
        inputScaleLabel.Text = inputScale.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
        if (core?.Latest is not VideoFrame frame) { inputResolution.Text = "Native resolution × " + inputScaleLabel.Text[..^1] + " · start a game to preview"; return; }
        int width = GraphicsLayout.ScaleDimension(frame.Width, inputScale.Value), height = GraphicsLayout.ScaleDimension(frame.Height, inputScale.Value);
        inputResolution.Text = $"{frame.Width} × {frame.Height} → {width} × {height}";
    }
    void UpdateAudio(bool save = true)
    {
        preferences.Audio = new(volume.Value, muted.IsChecked == true); core?.SetAudio(preferences.Audio);
        volume.ToolTip = $"Volume {volume.Value:F0}%"; muted.ToolTip = muted.IsChecked == true ? "Unmute" : "Mute";
        if (save) { preferencesTimer.Stop(); preferencesTimer.Start(); }
    }
    void SelectEnvironment(EnvironmentProfile? profile)
    {
        CommitCpuControls(); selected = profile; changing = true;
        pickerEnvironmentId = profile?.Id; pickerPath = profile?.Startup ?? "";
        UpdateProgramPicker();
        name.Text = profile?.Name ?? ""; startup.Text = profile?.Startup ?? ""; LoadCpuControls(profile);
        if (profile != null && !memory.Items.Contains(profile.Memory)) memory.Items.Add(profile.Memory); memory.SelectedItem = profile?.Memory ?? 32;
        drives.ItemsSource = profile?.Drives;
        var effects = profile?.Effects ?? new(); LoadPhoenixPanel(effects.Phoenix); enabled.IsChecked = effects.Enabled; compare.IsChecked = effects.Compare; intensity.Value = effects.Intensity; tone.Value = effects.Tone; structure.Value = effects.Structure; style.SelectedIndex = effects.Style; passes.SelectedItem = effects.Passes;
        renderer.SelectedIndex = effects.Renderer == "nvidia" ? 1 : 0; nvidiaUpscaling.IsChecked = effects.NvidiaUpscaling; nvidiaFrameGeneration.IsChecked = effects.NvidiaFrameGeneration;
        upscaleOrder.SelectedIndex = effects.UpscaleOrder == "before_nr" ? 1 : 0; upscaleFactor.SelectedItem = Math.Clamp(effects.UpscaleFactor, 2, 4);
        upscaleOrder.IsEnabled = upscaleFactor.IsEnabled = effects.NvidiaUpscaling;
        inputResize.IsChecked = effects.InputResize; inputScale.Value = Math.Clamp(effects.InputScale, .25, 4); inputFilter.SelectedIndex = effects.InputFilter == "linear" ? 1 : 0;
        changing = false; UpdateEffects(); RefreshBrowserDrives();
    }
    void RefreshBrowserDrives() { browseDrive.ItemsSource = null; browseDrive.ItemsSource = selected?.Drives; if (browseDrive.Items.Count > 0) browseDrive.SelectedIndex = 0; else { files.ItemsSource = null; browserPath.Text = "Add a drive to browse files."; } RefreshPrograms(); }
    void Browse(string directory)
    {
        if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(directory)) return;
        directory = Path.GetFullPath(directory);
        if (directory != driveRoot && !directory.StartsWith(driveRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;
        currentDirectory = directory; browserPath.Text = (browseDrive.SelectedItem as Drive)?.Letter + ":\\" + (directory == driveRoot ? "" : Path.GetRelativePath(driveRoot, directory));
        var drive = (Drive)browseDrive.SelectedItem;
        files.ItemsSource = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(f => f is DirectoryInfo || new[] { ".exe", ".com", ".bat" }.Contains(f.Extension.ToLowerInvariant()))
            .Select(f =>
            {
                var relative = Path.GetRelativePath(driveRoot, f.FullName);
                var friendly = drive.DisplayNames.GetValueOrDefault(relative, f.Name);
                return new BrowserEntry(f, (f is DirectoryInfo ? "📁 " : "▶ ") + friendly);
            }).OrderBy(f => f.Item is DirectoryInfo ? 0 : 1).ThenBy(f => f.Name).ToList();
    }
    void RunSelectedGame()
    {
        if (selected == null || browseDrive.SelectedItem is not Drive drive || files.SelectedItem is not BrowserEntry entry)
            throw new InvalidOperationException("Select a game folder or DOS program first.");
        var relative = Path.GetRelativePath(driveRoot, entry.Item.FullName);
        if (entry.Item is DirectoryInfo)
        {
            if (!drive.Launchers.TryGetValue(relative, out var program))
                throw new InvalidOperationException("Open this folder and select its DOS program. This entry has no configured DOS launcher.");
            relative = Path.Combine(relative, program);
        }
        selected.Startup = drive.Letter + ":\\" + relative; startup.Text = selected.Startup; LaunchSelected();
    }
    public void OpenEnvironmentBrowser(string? id = null)
    {
        var profile = id == null ? selected ?? library.Environments.FirstOrDefault() : library.Environments.FirstOrDefault(e => e.Id == id);
        if (profile == null) throw new ArgumentException("Environment not found.");
        environments.SelectedItem = profile; ((TabControl)deck.Children[0]).SelectedIndex = 2;
    }
    void CreateFolder()
    {
        if (browseDrive.SelectedItem is not Drive || !Directory.Exists(currentDirectory))
            throw new InvalidOperationException("Select a mapped drive in the Files tab first.");
        var parent = currentDirectory;
        var dialog = new Window { Owner = this, Title = "New folder", Width = 420, Height = 220,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Label("Create inside " + browserPath.Text));
        var folderName = new TextBox(); panel.Children.Add(folderName);
        var create = Button("Create folder", () =>
        {
            var path = DriveFolders.Create(driveRoot, parent, folderName.Text);
            Browse(path); dialog.Close();
        });
        create.IsDefault = true; panel.Children.Add(create);
        var cancel = new Button { Content = "Cancel", IsCancel = true }; panel.Children.Add(cancel);
        dialog.Content = panel; dialog.Loaded += (_, _) => folderName.Focus(); dialog.ShowDialog();
    }
    void SaveSelected()
    {
        if (selected == null) return;
        CommitCpuControls(); selected.Name = string.IsNullOrWhiteSpace(name.Text) ? "DOS environment" : name.Text.Trim(); selected.Startup = startup.Text.Trim(); selected.Memory = memory.SelectedItem is int n ? n : 32; selected.Effects = phoenix.Settings;
        Configuration.Generate(selected); library.Save(); environments.Items.Refresh(); pickerEnvironmentId = selected.Id; pickerPath = selected.Startup; UpdateProgramPicker();
    }
    void NewEnvironment()
    {
        var profile = new EnvironmentProfile { Name = "DOS workspace " + (library.Environments.Count + 1) };
        var folder = Path.Combine(Paths.Data, "environments", profile.Id, "drive-c"); Directory.CreateDirectory(folder); profile.Drives.Add(new() { Folder = folder });
        library.Environments.Add(profile); library.Save(); environments.SelectedItem = profile;
    }
    void Duplicate()
    {
        SaveSelected(); if (selected == null) return;
        var clone = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(selected))!; clone.Id = Guid.NewGuid().ToString("N"); clone.Name += " (copy)";
        library.Environments.Add(clone); library.Save(); environments.SelectedItem = clone;
    }
    void Remove() { if (selected != null) { library.Environments.Remove(selected); library.Save(); if (library.Environments.Count > 0) environments.SelectedIndex = 0; } }
    void EditDrive(Drive? existing)
    {
        if (selected == null) { NewEnvironment(); }
        var dialog = new Window { Owner = this, Title = existing == null ? "Add DOS drive" : "Edit DOS drive", Width = 540, Height = 280, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(16) }; var letter = new ComboBox(); foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXY") letter.Items.Add(c.ToString());
        letter.SelectedItem = existing?.Letter ?? "CDEFGHIJKLMNOPQRSTUVWXYAB".First(c => !selected!.Drives.Any(d => d.Letter == c.ToString())).ToString();
        var path = new TextBox { Text = existing?.Folder ?? "" }; var type = new ComboBox { ItemsSource = new[] { "dir", "cdrom" }, SelectedItem = existing?.Type ?? "dir" };
        panel.Children.Add(letter); panel.Children.Add(path); panel.Children.Add(Button("Choose folder…", () => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) path.Text = picker.FolderName; })); panel.Children.Add(type);
        panel.Children.Add(Button("Save drive", () =>
        {
            var sameFolder = existing != null && string.Equals(Path.GetFullPath(path.Text), Path.GetFullPath(existing.Folder), StringComparison.OrdinalIgnoreCase);
            var drive = new Drive { Letter = (string)letter.SelectedItem, Folder = path.Text, Type = (string)type.SelectedItem,
                Label = existing?.Label ?? "", DisplayNames = sameFolder ? existing!.DisplayNames : [], Launchers = sameFolder ? existing!.Launchers : [] };
            var candidate = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(selected))!; if (existing != null) candidate.Drives.RemoveAt(selected!.Drives.IndexOf(existing)); candidate.Drives.Add(drive); Configuration.Generate(candidate);
            if (existing != null) selected!.Drives.Remove(existing); selected!.Drives.Add(drive); SaveSelected(); RefreshBrowserDrives(); dialog.Close();
        })); dialog.Content = panel; dialog.ShowDialog();
    }
    void OpenQuick()
    {
        var dialog = new OpenFileDialog { Title = "Open a DOS program", Filter = "DOS programs|*.exe;*.com;*.bat" };
        if (dialog.ShowDialog(this) == true) QuickRun(dialog.FileName);
    }
    public void QuickRun(string executable)
    {
        var profile = Configuration.Quick(executable);
        // A repeatable quick session key keeps save states available next time.
        profile.Id = "quick-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToLowerInvariant())))[..16].ToLowerInvariant();
        profile.Effects = phoenix.Settings; Launch(profile);
    }
    void LaunchSelected() { SaveSelected(); if (selected == null) { NewEnvironment(); } Launch(selected!); }
    public void Launch(EnvironmentProfile profile)
    {
        var config = Configuration.Generate(profile); Stop(); running = profile;
        changing = true;
        var effects = profile.Effects; LoadPhoenixPanel(effects.Phoenix); enabled.IsChecked = effects.Enabled; compare.IsChecked = effects.Compare;
        intensity.Value = effects.Intensity; tone.Value = effects.Tone; structure.Value = effects.Structure;
        style.SelectedIndex = effects.Style; passes.SelectedItem = effects.Passes; renderer.SelectedIndex = effects.Renderer == "nvidia" ? 1 : 0;
        nvidiaUpscaling.IsChecked = effects.NvidiaUpscaling; nvidiaFrameGeneration.IsChecked = effects.NvidiaFrameGeneration;
        inputResize.IsChecked = effects.InputResize; inputScale.Value = Math.Clamp(effects.InputScale, .25, 4); inputFilter.SelectedIndex = effects.InputFilter == "linear" ? 1 : 0; inputResizeOptions.Visibility = effects.InputResize ? Visibility.Visible : Visibility.Collapsed;
        upscaleOrder.SelectedIndex = effects.UpscaleOrder == "before_nr" ? 1 : 0; upscaleFactor.SelectedItem = Math.Clamp(effects.UpscaleFactor, 2, 4);
        upscaleOrder.IsEnabled = upscaleFactor.IsEnabled = effects.NvidiaUpscaling;
        phoenix.Settings = effects; passes.IsEnabled = effects.Renderer != "nvidia"; changing = false;
        var directory = Path.Combine(Paths.Data, "sessions", profile.Id); Directory.CreateDirectory(directory);
        runningConfig = config; var path = Path.Combine(directory, "environment.conf"); Paths.AtomicWrite(path, Configuration.Generate(profile, liveCpu: true));
        coreCatalogStored = false;
        string cpuValue = CpuCycles.Effective(profile);
        core = new DosCore(path, Path.Combine(directory, "saves"), coreOptions: profile.DosboxSettings.GetValueOrDefault("libretro"), initialCycles: CpuCycles.IsSimple(cpuValue) ? cpuValue : null); core.OnMessage = text => Dispatcher.BeginInvoke(() => Notice(text));
        core.SetAudio(preferences.Audio);
        welcome.Visibility = Visibility.Collapsed; if (!BackgroundAutomation) stage.Focus(); phoenix.Reset(); submitted = shown = enhancedSeen = -1; displayed = null;
    }
    public void Stop()
    {
        ReleaseMouseCapture();
        core?.Dispose(); core = null; running = null; submitted = shown = enhancedSeen = -1; image.Source = null; displayed = null; nativeView.Visibility = Visibility.Hidden; welcome.Visibility = Visibility.Visible; phoenix.Reset(); status.Text = "Ready · open a program or launch an environment";
    }
    public async Task State(bool save)
    {
        try { await StateOperation(save); } catch (Exception e) { Notice(e.Message); }
    }
    async Task StateOperation(bool save)
    {
            var active = core; var profile = running;
            if (active == null || profile == null) return;
            var directory = Path.Combine(Paths.Data, "sessions", profile.Id); var state = Path.Combine(directory, "quick.state");
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(runningConfig + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Paths.Runtime, "dosbox_pure_libretro.dll")))))));
            if (save) { await active.SaveState(state); Paths.AtomicWrite(state + ".json", JsonSerializer.Serialize(new { fingerprint, saved = DateTimeOffset.Now, profile = profile.Name })); }
            else { using var meta = JsonDocument.Parse(File.ReadAllText(state + ".json")); if (meta.RootElement.GetProperty("fingerprint").GetString() != fingerprint) throw new InvalidOperationException("The saved state belongs to a different drive, startup or core configuration."); await active.LoadState(state); phoenix.Reset(); }
            status.Text = save ? "State saved" : "State loaded";
    }
    void Tick()
    {
        if (disposed) return;
        CacheCoreCatalog();
        TickMouseCapture();
        var source = core?.Latest;
        if (inputResize.IsChecked == true) UpdateInputPreview();
        var wasPaused = phoenix.Paused; var wasMenu = phoenix.MenuOpen; var wasBackground = phoenix.Background;
        phoenix.Paused = core?.IsPaused == true;
        phoenix.Background = !nativeView.PresentationActive;
        phoenix.MenuOpen = fullscreen && (menu.Visibility == Visibility.Visible || panels) || !nativeView.PlacementSettled;
        if (wasPaused != phoenix.Paused || wasMenu != phoenix.MenuOpen || wasBackground != phoenix.Background) submitted = -1;
        if (source != null)
        {
            if (source.Sequence != submitted) { phoenix.Submit(source); submitted = source.Sequence; }
            var enhanced = phoenix.Latest;
            if (enhanced != null && enhanced.Sequence != enhancedSeen) { enhancedSeen = enhanced.Sequence; processedAt = clock.Elapsed.TotalSeconds; }
            // Keep emulation responsive even while a model initializes or fails.
            var frame = enhanced != null && enhanced.SourceWidth == source.Width && enhanced.SourceHeight == source.Height && clock.Elapsed.TotalSeconds - processedAt < .5 ? enhanced : source;
            nativeView.Visibility = phoenix.Status.StartsWith("Graphics unavailable") ? Visibility.Hidden : Visibility.Visible;
            if (displayed != frame) Present(frame);
            status.Text = $"{running?.Name} · DOS {source.Width} × {source.Height} → display {(int)image.ActualWidth} × {(int)image.ActualHeight} · {(core!.IsPaused ? "Paused" : "Running")} · {phoenix.Status}" + (mouseCaptured ? " · Mouse captured · " + releaseShortcut + " to release" : "");
        }
        else { nativeView.Visibility = Visibility.Hidden; if (core != null) status.Text = core.Status; }
        phoenixStatus.Text = phoenix.Status;
        nvidiaStatus.Text = phoenix.UpscalingStatus + "\n" + phoenix.FrameGenerationStatus;
        if (source != null && clock.Elapsed.TotalSeconds < captureNoticeUntil) status.Text += "\n" + captureNotice;
        UpdateMcpUi();
        nativeView.UpdatePlacement();
        if (fullscreen)
        {
            GetCursorPos(out var p); var local = PointFromScreen(new Point(p.X, p.Y));
            bool top = !mouseCaptured && (IsActive || nativeView.PresentationActive) && local.X >= 0 && local.X <= ActualWidth && local.Y >= 0 && local.Y <= 6;
            if (dwell.Update(local.X, local.Y, clock.Elapsed.TotalSeconds, top)) ShowMenu(true);
            if (menu.Visibility == Visibility.Visible && !panels)
            {
                if (local.Y <= menu.ActualHeight + 12 || menu.IsKeyboardFocusWithin || programPicker.IsOpen || screenshotPopup?.IsOpen == true) hideSince = clock.Elapsed.TotalSeconds;
                else if (clock.Elapsed.TotalSeconds - hideSince > .75) ShowMenu(false);
            }
        }
    }
    void Present(VideoFrame frame)
    {
        if (bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height) bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0); image.Source = bitmap;
        displayed = frame; shown = frame.Sequence; FitImage();
    }
    void FitImage()
    {
        if (displayed == null) return;
        var sourceWidth = displayed.SourceWidth > 0 ? displayed.SourceWidth : displayed.Width;
        var sourceHeight = displayed.SourceHeight > 0 ? displayed.SourceHeight : displayed.Height;
        var ratio = aspectMode.SelectedIndex == 1 ? (double)sourceWidth / sourceHeight : displayed.Aspect;
        double width = stage.ActualWidth, height = stage.ActualHeight;
        if (aspectMode.SelectedIndex != 2) { if (width / Math.Max(1, height) > ratio) width = height * ratio; else height = width / ratio; }
        if (scaler.SelectedIndex == 2)
        {
            var scale = Math.Floor(Math.Min(width / sourceWidth, height / sourceHeight));
            if (scale >= 1) { width = sourceWidth * scale; height = sourceHeight * scale; }
        }
        image.Width = Math.Max(1, width); image.Height = Math.Max(1, height);
        nativeView.Width = image.Width; nativeView.Height = image.Height;
        RenderOptions.SetBitmapScalingMode(image, scaler.SelectedIndex == 0 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
    }
    public void ToggleFullscreen()
    {
        if (BackgroundAutomation) return;
        programPicker.Close();
        ReleaseMouseCapture();
        fullscreen = !fullscreen; environmentVisible = fullscreen ? false : preferences.EnvironmentVisible; effectsVisible = fullscreen ? false : preferences.EffectsVisible;
        if (fullscreen) { previousState = WindowState; root.Children.Remove(menu); fullscreenMenu.Child = menu; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; ShowMenu(false); }
        else { fullscreenMenu.IsOpen = false; fullscreenMenu.Child = null; root.Children.Add(menu); WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = previousState; ShowMenu(true); }
        dwell.Reset(); ApplyLayout();
    }
    void ApplyLayout()
    {
        panels = environmentVisible || effectsVisible;
        deck.ColumnDefinitions[0].Width = new GridLength(environmentVisible ? 285 : 0); deck.ColumnDefinitions[2].Width = new GridLength(effectsVisible ? 270 : 0);
        deck.Children[0].Visibility = environmentVisible ? Visibility.Visible : Visibility.Collapsed; deck.Children[2].Visibility = effectsVisible ? Visibility.Visible : Visibility.Collapsed;
        status.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(deck, fullscreen && !panels ? 0 : 1); Grid.SetRowSpan(deck, fullscreen && !panels ? 3 : 1);
        deck.Margin = fullscreen && panels ? new Thickness(0, menu.ActualHeight, 0, 0) : new Thickness(0);
        UpdatePaneButtons();
        FitImage();
    }
    void ShowMenu(bool value)
    {
        if (!value) programPicker.Close();
        if (value) ReleaseMouseCapture();
        var changed = menu.Visibility != (value ? Visibility.Visible : Visibility.Collapsed);
        menu.Visibility = value ? Visibility.Visible : Visibility.Collapsed; hideSince = clock.Elapsed.TotalSeconds;
        if (fullscreen)
        {
            var screen = PointToScreen(new Point(0, 0)); fullscreenMenu.HorizontalOffset = screen.X; fullscreenMenu.VerticalOffset = screen.Y;
            menu.Width = ActualWidth; fullscreenMenu.IsOpen = value;
        }
        else menu.Width = double.NaN;
        if (value && changed) { core?.ReleaseKeys(); ReleaseCapture(); }
    }
    public bool TestTopPointer(double x, double y, double seconds)
    {
        if (!mouseCaptured && dwell.Update(x, y, seconds, y >= 0 && y <= 6)) ShowMenu(true);
        return menu.Visibility == Visibility.Visible;
    }
    public async Task<Dictionary<string, object>> ValidateAudioAndRenderer()
    {
        var originalAudio = preferences.Audio; var originalRenderer = renderer.SelectedIndex;
        var sr = nvidiaUpscaling.IsChecked; var fg = nvidiaFrameGeneration.IsChecked;
        var checks = new Dictionary<string, object>();
        try
        {
            volume.Value = 50; muted.IsChecked = false; await Task.Delay(150);
            checks["half_volume_gain"] = core?.ActualAudioGain ?? double.NaN;
            muted.IsChecked = true; await Task.Delay(150);
            checks["muted_gain"] = core?.ActualAudioGain ?? double.NaN;
            renderer.SelectedIndex = 1;
            for (int i = 0; i < 120 && !phoenix.Status.StartsWith("NVIDIA DLSS5 active"); i++) await Task.Delay(100);
            Tick();
            checks["nvidia_selection"] = phoenix.Settings.Renderer == "nvidia";
            checks["nvidia_active"] = phoenix.Status.StartsWith("NVIDIA DLSS5 active");
            checks["nvidia_status"] = phoenix.Status;
            checks["upscaling_status"] = phoenix.UpscalingStatus;
            checks["frame_generation_status"] = phoenix.FrameGenerationStatus;
            checks["nvidia_options_retained"] = nvidiaUpscaling.IsChecked == sr && nvidiaFrameGeneration.IsChecked == fg;
            var directory = Path.Combine(Paths.Root, "tests", "results"); Directory.CreateDirectory(directory);
            CaptureWindow(Path.Combine(directory, "nvidia-controls.png"));
            renderer.SelectedIndex = 0;
            for (int i = 0; i < 120 && !phoenix.Status.StartsWith("Phoenix DLSS5 active"); i++) await Task.Delay(100);
            checks["phoenix_active"] = phoenix.Status.StartsWith("Phoenix DLSS5 active");
            checks["phoenix_options_retained"] = nvidiaUpscaling.IsChecked == sr && nvidiaFrameGeneration.IsChecked == fg;
        }
        finally
        {
            renderer.SelectedIndex = originalRenderer; volume.Value = originalAudio.Volume; muted.IsChecked = originalAudio.Muted;
            preferences.Save();
        }
        return checks;
    }
    public void Screenshot()
    {
        if (displayed != null) SaveScreenshot(displayed);
    }
    public void CaptureWindow(string path)
    {
        var nativeVisible = nativeView.Visibility; nativeView.Visibility = Visibility.Hidden;
        ((FrameworkElement)Content).UpdateLayout();
        var target = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render((Visual)Content);
        BitmapSource captured = target;
        if (fullscreen && fullscreenMenu.IsOpen && menu.ActualHeight > 0)
        {
            var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); menuBitmap.Render(menu);
            var composite = new DrawingVisual(); using (var draw = composite.RenderOpen()) { draw.DrawImage(target, new Rect(0, 0, target.PixelWidth, target.PixelHeight)); draw.DrawImage(menuBitmap, new Rect(0, 0, menuBitmap.PixelWidth, menuBitmap.PixelHeight)); }
            var combined = new RenderTargetBitmap(target.PixelWidth, target.PixelHeight, 96, 96, PixelFormats.Pbgra32); combined.Render(composite); captured = combined;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(captured)); using var stream = File.Create(path); encoder.Save(stream);
        nativeView.Visibility = nativeVisible;
    }
    public object RenderDiagnostics() => new { fullscreen, menuVisible = menu.Visibility == Visibility.Visible,
        stageWidth = stage.ActualWidth, stageHeight = stage.ActualHeight, imageWidth = image.ActualWidth, imageHeight = image.ActualHeight,
        sourceWidth = displayed?.Width, sourceHeight = displayed?.Height, nonblack = displayed?.Pixels.Where((b, i) => i % 4 != 3 && b > 0).Count() };
    void GameKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key; uint code = MapKey(key); if (code == 0 || core == null) return;
        ushort modifiers = (ushort)((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 64 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 256 : 0));
        uint character = code is >= 32 and < 127 ? code : 0;
        if (character is >= 97 and <= 122 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) character -= 32;
        core.Key(code, down, down ? character : 0, modifiers); e.Handled = true;
    }
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    public bool FocusForValidation(Window? target = null)
    {
        if (BackgroundAutomation) return false;
        var focusWindow = target ?? this;
        var hwnd = new WindowInteropHelper(focusWindow).Handle;
        var other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero); var own = GetCurrentThreadId();
        bool attached = other != own && AttachThreadInput(own, other, true);
        try { SetForegroundWindow(hwnd); focusWindow.Activate(); }
        finally { if (attached) AttachThreadInput(own, other, false); }
        return nativeView.PresentationActive;
    }
    public async Task TestNativeKeyboard()
    {
        PostMessage(nativeView.Surface, 0x100, new IntPtr(65), IntPtr.Zero);
        await Task.Delay(80);
        PostMessage(nativeView.Surface, 0x101, new IntPtr(65), IntPtr.Zero);
        await Task.Delay(250);
    }
    void NativeGameInput(int message, IntPtr wParam, IntPtr lParam)
    {
        if (core == null) return;
        if (message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            bool down = message is 0x100 or 0x104; var key = KeyInterop.KeyFromVirtualKey((int)wParam);
            if (HandleMouseReleaseKey(key, Keyboard.Modifiers, down)) return;
            if (down && key == Key.F11) { ToggleFullscreen(); return; }
            if (down && key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ToggleFullscreen(); return; }
            if (down && key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ShowMenu(true); SetAllPanels(!panels); core.ReleaseKeys(); return; }
            var code = MapKey(key);
            ushort modifiers = (ushort)((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 64 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 256 : 0));
            var character = down && code is >= 32 and < 127 ? code : 0;
            if (character is >= 97 and <= 122 && (modifiers & 1) != 0) character -= 32;
            if (code != 0) core.Key(code, down, character, modifiers);
        }
        else
        {
            var value = lParam.ToInt64(); var p = new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
            if (message == 0x200 && !mouseCaptured && !preferences.Mouse.CaptureOnClick && stage.IsKeyboardFocusWithin && previousMouse is Point old) { var source = core.Latest; core.MouseMove((int)((p.X-old.X)*(source?.Width??640)/Math.Max(1,nativeView.PixelWidth)), (int)((p.Y-old.Y)*(source?.Height??400)/Math.Max(1,nativeView.PixelHeight))); }
            if (message == 0x200) previousMouse = p;
            if (message is 0x201 or 0x204 or 0x207) { stage.Focus(); if (!mouseCaptured && preferences.Mouse.CaptureOnClick) Try(() => CaptureGameMouse()); else { if (!mouseCaptured) SetCapture(nativeView.Surface); core.MouseButton(message == 0x201 ? 1 : message == 0x207 ? 4 : 2, true); } }
            if (message is 0x202 or 0x205 or 0x208) { if (!mouseCaptured) ReleaseCapture(); if (mouseCaptured || !preferences.Mouse.CaptureOnClick) core.MouseButton(message == 0x202 ? 1 : message == 0x208 ? 4 : 2, false); }
            if (message == 0x20a && (mouseCaptured || !preferences.Mouse.CaptureOnClick)) core.MouseWheel(Math.Sign((short)((wParam.ToInt64()>>16)&0xffff)));
            if (message == 8 || message == 0x1f || (message == 0x215 && GetCapture() != nativeView.Surface)) { ReleaseMouseCapture(); core.ReleaseKeys(); previousMouse = null; }
            if (message == 0x2a3 && !mouseCaptured) previousMouse = null;
        }
    }
    public static uint MapKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (uint)(97 + key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return (uint)(48 + key - Key.D0);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(256 + key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F12) return (uint)(282 + key - Key.F1);
        return key switch { Key.Back => 8, Key.Tab => 9, Key.Enter => 13, Key.Escape => 27, Key.Space => 32, Key.Delete => 127,
            Key.Up => 273, Key.Down => 274, Key.Right => 275, Key.Left => 276, Key.Insert => 277, Key.Home => 278, Key.End => 279, Key.PageUp => 280, Key.PageDown => 281,
            Key.LeftShift => 304, Key.RightShift => 303, Key.LeftCtrl => 306, Key.RightCtrl => 305, Key.LeftAlt => 308, Key.RightAlt => 307, Key.CapsLock => 301, Key.NumLock => 300, Key.Scroll => 302,
            Key.OemMinus => 45, Key.OemPlus => 61, Key.OemComma => 44, Key.OemPeriod => 46, Key.OemQuestion => 47, Key.OemSemicolon => 59, Key.OemQuotes => 39, Key.OemOpenBrackets => 91, Key.OemCloseBrackets => 93, Key.OemPipe => 92, Key.OemTilde => 96,
            Key.Multiply => 268, Key.Add => 270, Key.Subtract => 269, Key.Decimal => 266, Key.Divide => 267, _ => 0 };
    }
    [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint point);
    void DisposeApp() { if (disposed) return; disposed = true; StopUpdateNotice(); programPicker.Close(); renameProgramWindow?.Close(); DisposeMouseCapture(); programTimer.Stop(); scanCancellation?.Cancel(); mcp?.Dispose(); mcpPanel?.Close(); timer.Stop(); fullscreenMenu.IsOpen = false; preferencesTimer.Stop(); preferences.Save(); core?.Dispose(); phoenix.Dispose(); }
}



using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    static void CaptureDialog(Window dialog, string path)
    {
        dialog.UpdateLayout();
        var target = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32); target.Render(dialog);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using var stream = File.Create(path); encoder.Save(stream);
    }
    static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var target = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); target.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using var stream = File.Create(path); encoder.Save(stream);
    }
    public async Task RunUiValidation()
    {
        var checks = new List<string>(); var report = new Dictionary<string, object?>();
        var original = selected; var savedEnvironmentVisible = preferences.EnvironmentVisible; var savedEffectsVisible = preferences.EffectsVisible;
        EnvironmentProfile? profile = null; var addedProfiles = new List<EnvironmentProfile>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        async Task Scanned() { for (int i = 0; i < 400 && scanningPrograms; i++) await Task.Delay(50); Check(!scanningPrograms, "program scan completed"); }
        var results = Path.Combine(Paths.Root, "tests", "results"); Directory.CreateDirectory(results);
        try
        {
            var fixture = Path.Combine(Paths.Root, "tests", "fixtures", "ui-" + Guid.NewGuid().ToString("N"));
            var c = Path.Combine(fixture, "c"); var d = Path.Combine(fixture, "d"); Directory.CreateDirectory(Path.Combine(c, "GAMES")); Directory.CreateDirectory(Path.Combine(d, "MORE"));
            File.WriteAllBytes(Path.Combine(c, "GAMES", "VERIFY.COM"), [0xeb, 0xfe]); // Original two-byte DOS loop fixture, generated locally.
            File.WriteAllText(Path.Combine(c, "GAMES", "SETUP.BAT"), "@echo off\r\necho SETUP\r\n"); File.WriteAllText(Path.Combine(d, "MORE", "OTHER.BAT"), "@echo off\r\necho OTHER\r\n");
            profile = new() { Id = "ui-test-" + Guid.NewGuid().ToString("N"), Name = "DOS workspace · UI validation", Startup = "C:\\GAMES\\VERIFY.COM", Drives = [new() { Folder = c }, new() { Letter = "D", Folder = d }], Effects = new(Enabled: false, NvidiaUpscaling: false, NvidiaFrameGeneration: false) };
            library.Environments.Add(profile); addedProfiles.Add(profile); environments.SelectedItem = profile; await Scanned();
            Check(profile.Programs.Count == 3 && profile.Programs.Any(p => p.Path.StartsWith("D:")), "dropdown discovers nested executables from both mapped drives");
            Check(programPicker.Choices.Count(c => c.Environment == profile) == 4, "picker includes discovered programs and DOS prompt");
            Check(programPicker.SelectedChoice?.Program.Path == profile.Startup, "switching environments selects its own saved startup program");
            Check(menu.Children.OfType<Button>().All(b => b.Content is Viewbox && b.ToolTip != null), "every toolbar button uses a vector icon with a tooltip");
            Check(menu.Children[menu.Children.IndexOf(programPicker) + 1] is Button play && play.ToolTip?.ToString() == "Run selected program / launch environment", "Play immediately follows the program picker");
            Check(!menu.Children.OfType<Button>().Any(b => b.ToolTip?.ToString()?.Contains("Copy this") == true || b.ToolTip?.ToString()?.Contains("DOSBox configuration") == true), "copy and DOSBox configuration are absent from the toolbar");
            SetAllPanels(true); UpdateLayout(); double width = stage.ActualWidth;
            Check(Descendants<Button>(deck.Children[0]).Any(b => Equals(b.Content, "Copy environment")) && Descendants<Button>(deck.Children[0]).Any(b => Equals(b.Content, "DOSBox configuration…")), "copy and DOSBox configuration live in the environment tab");
            Click(environmentButton!); UpdateLayout(); Check(!environmentVisible && effectsVisible && stage.ActualWidth > width + 200, "environment icon hides only the environment and expands the game");
            width = stage.ActualWidth; Click(effectsButton!); UpdateLayout(); Check(!effectsVisible && stage.ActualWidth > width + 200, "beautify icon independently hides effects and expands the game");
            Click(environmentButton!); Click(effectsButton!); UpdateLayout();
            nvidiaUpscaling.IsChecked = true; upscaleOrder.SelectedIndex = 1; upscaleFactor.SelectedItem = 3;
            Check(phoenix.Settings.UpscaleOrder == "before_nr" && phoenix.Settings.UpscaleFactor == 3 && upscaleOrder.IsEnabled && upscaleFactor.IsEnabled, "processing order and size controls update the selected environment");
            var savedEffects = Library.Load().Environments.Single(p => p.Id == profile.Id).Effects;
            Check(savedEffects.UpscaleOrder == "before_nr" && savedEffects.UpscaleFactor == 3, "processing order and size survive disk reload");
            nvidiaUpscaling.IsChecked = false;
            Check(!upscaleOrder.IsEnabled && !upscaleFactor.IsEnabled && !phoenix.Settings.NvidiaUpscaling && phoenix.Settings.UpscaleOrder == "before_nr", "native mode disables upscale controls while retaining the chosen order");
            inputResize.IsChecked = true; inputScale.Value = 1.25; inputFilter.SelectedIndex = 1;
            Check(phoenix.Settings.InputResize && phoenix.Settings.InputScale == 1.25 && phoenix.Settings.InputFilter == "linear" && inputResizeOptions.Visibility == Visibility.Visible && inputScaleLabel.Text == "1.25×", "fractional input slider exposes independent size and filter controls");
            var inputSaved = Library.Load().Environments.Single(p => p.Id == profile.Id).Effects;
            Check(inputSaved.InputResize && inputSaved.InputScale == 1.25 && inputSaved.InputFilter == "linear" && !inputSaved.NvidiaUpscaling, "input resize saves independently of NVIDIA upscaling");
            bool sliderCaptured = Mouse.Capture(inputScale); inputScale.Value = 1.5;
            Check(sliderCaptured && phoenix.Settings.InputScale == 1.25 && inputScaleLabel.Text == "1.5×", "dragging previews a fractional size without rebuilding graphics");
            Mouse.Capture(null);
            Check(phoenix.Settings.InputScale == 1.5, "releasing the resize slider applies its previewed size");
            inputScale.Value = 1.25;
            inputResize.IsChecked = false;
            Check(!phoenix.Settings.InputResize && inputResizeOptions.Visibility == Visibility.Collapsed && phoenix.Settings.InputScale == 1.25, "turning input resize off restores native input while retaining its last size");
            ShowProgramManager(); await Task.Delay(150);
            var list = Descendants<ListBox>(programManager!).Single();
            var first = (ListBoxItem)list.Items[0]; var entry = (ProgramEntry)first.Tag; string hiddenPath = entry.Path;
            first.IsSelected = true;
            var checkbox = Descendants<CheckBox>(first).Single(); checkbox.ApplyTemplate();
            Check(checkbox.Content is StackPanel && checkbox.Template.FindName("Tick", checkbox) is System.Windows.Shapes.Path { Visibility: Visibility.Visible }, "checkbox uses the full label as its hit area and a visible checked tick");
            FocusForValidation(programManager); await Task.Delay(100); checkbox.Focus(); await Task.Delay(50);
            report["checkbox_focused"] = checkbox.IsKeyboardFocused; report["manager_active"] = programManager!.IsActive;
            SendValidationInput(KeyInput(32, true), KeyInput(32, false)); await Task.Delay(120);
            CaptureDialog(programManager!, Path.Combine(results, "checkbox-input.png"));
            Check(checkbox.IsChecked == false && !entry.Visible && checkbox.Template.FindName("Tick", checkbox) is System.Windows.Shapes.Path { Visibility: Visibility.Collapsed }, "Space toggles the focused checkbox exactly once and hides its tick");
            // Resolve the actual label hit target, then use WPF's accessibility input.
            // This doesn't borrow the user's desktop pointer during a release build.
            var labelPoint = ((FrameworkElement)checkbox.Content).TranslatePoint(new Point(55, 18), checkbox);
            DependencyObject? hit = VisualTreeHelper.HitTest(checkbox, labelPoint)?.VisualHit;
            while (hit != null && hit != checkbox) hit = VisualTreeHelper.GetParent(hit);
            var toggle = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            Check(hit == checkbox && checkbox.IsChecked == true && entry.Visible, "program label hit-test reaches its checkbox and accessible activation toggles it once");
            toggle.Toggle();
            Check(checkbox.IsChecked == false && !entry.Visible, "second accessible activation reliably unchecks the program");
            var renamer = Descendants<TextBox>(programManager!).First(t => t.ToolTip?.ToString()?.Contains("Display name") == true); renamer.Text = "My custom launcher";
            Click(Descendants<Button>(programManager!).Single(b => Equals(b.Content, "Rename"))); Click(Descendants<Button>(programManager!).Single(b => Equals(b.Content, "Move down")));
            CaptureDialog(programManager!, Path.Combine(results, "program-manager.png")); Click(Descendants<Button>(programManager!).Single(b => Equals(b.Content, "Save program menu")));
            Check(!profile.Programs.Single(p => p.Path == hiddenPath).Visible && profile.Programs[1].Path == hiddenPath && profile.Programs[1].Name == "My custom launcher", "manager saves hidden entries, custom names and order");
            File.WriteAllText(Path.Combine(d, "MORE", "NEW.BAT"), "@echo off\r\necho NEW\r\n"); RefreshPrograms(); await Scanned();
            Check(profile.Programs.Count == 4 && profile.Programs[^1].Path.EndsWith("NEW.BAT") && !profile.Programs[1].Visible && profile.Programs[1].Name == "My custom launcher", "rescan appends new programs without changing hidden entries, names or order");
            Check(!programPicker.Choices.Any(p => p.Environment == profile && p.Program.Path == hiddenPath), "hidden program stays absent from the picker");
            profile.DiscoverPrograms = false; File.WriteAllText(Path.Combine(d, "MORE", "LATER.BAT"), "echo later"); RefreshPrograms(); await Scanned();
            Check(profile.Programs.Count == 4, "disabling automatic additions retains only the managed list");
            library.Save(); var persisted = Library.Load().Environments.Single(p => p.Id == profile.Id); Check(!persisted.Programs[1].Visible && persisted.Programs[1].Name == "My custom launcher" && !persisted.DiscoverPrograms, "program settings and ordering survive disk reload");
            // Exercise the Play icon with a program from the dropdown.
            programPicker.Choose(programPicker.Choices.Single(p => p.Environment == profile && p.Program.Path.EndsWith("OTHER.BAT")));
            Click(menu.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == "Run selected program / launch environment"));
            for (int i = 0; i < 100 && core?.Latest == null; i++) await Task.Delay(50);
            Check(running?.Startup == "D:\\MORE\\OTHER.BAT" && core?.IsReady == true, "Play launches the selected program from another drive");
            Stop(); startup.Text = "C:\\GAMES\\VERIFY.COM"; SaveSelected(); Launch(profile);
            for (int i = 0; i < 100 && core?.Latest == null; i++) await Task.Delay(50);
            Check(core?.IsReady == true && CoreCatalog().Count > 10, "embedded DOS engine exposes its full option catalog");
            CacheCoreCatalog(); Stop();
            DosboxIni.Set(profile.DosboxSettings, "custom", "preserve", "yes"); ShowDosboxSettings(); await Task.Delay(150);
            var config = configurationWindow!; var cpu = Descendants<ComboBox>(config).Single(t => t.ToolTip?.ToString()?.StartsWith("[cpu] core") == true); cpu.Text = "normal";
            var ram = Descendants<ComboBox>(config).Single(t => t.ToolTip?.ToString()?.StartsWith("[dosbox] memsize") == true); ram.Text = "12";
            var cyclesInput = Descendants<ComboBox>(config).Single(t => t.ToolTip?.ToString()?.StartsWith("[cpu] cycles") == true); cyclesInput.Text = "max 80% limit 50000";
            Check(Descendants<ComboBox>(config).Count() > 60, "configuration contains grouped DOS settings and installed core options");
            CaptureDialog(config, Path.Combine(results, "dosbox-configuration.png"));
            var categories = Descendants<ListBox>(config).Single(); categories.SelectedItem = "Advanced INI"; await Task.Delay(50);
            var ini = Descendants<TextBox>(config).Single(t => t.AcceptsReturn); Check(ini.Text.Contains("preserve=yes") && ini.Text.Contains("cycles=max 80% limit 50000") && ini.Text.Contains("memsize=12"), "structured controls and advanced INI synchronize without losing extra keys");
            ini.Text += "\r\n[mixer]\r\nrate=22050\r\n"; categories.SelectedItem = "Mixer & MIDI"; await Task.Delay(50);
            Check(Descendants<ComboBox>(config).Single(t => t.ToolTip?.ToString()?.StartsWith("[mixer] rate") == true).Text == "22050", "advanced INI updates structured controls");
            Click(Descendants<Button>(config).Single(b => Equals(b.Content, "Save & reload DOS")));
            for (int i = 0; i < 100 && core?.Latest == null; i++) await Task.Delay(50);
            Check(core?.IsReady == true && runningConfig.Contains("memsize=12") && runningConfig.Contains("core=normal") && runningConfig.Contains("rate=22050") && runningConfig.Contains("cycles=max 80% limit 50000"), "saved CPU, custom cycle syntax, RAM and audio settings reach the relaunched engine");
            Check(profile.DosboxSettings["custom"]["preserve"] == "yes" && Equals(memory.SelectedItem, 12), "unknown configuration keys survive save and RAM controls stay synchronized");
            // A DOS program queries the XMS manager and writes its answer through DOS file I/O,
            // proving the memory setting changed the machine rather than only the INI.
            Stop();
            var memoryProbe = new List<byte> { 0xb8,0x10,0x43,0xcd,0x2f,0x89,0x1e,0,0,0x8c,0x06,0,0,0xb4,0x08,0xff,0x1e,0,0,0x89,0x16,0,0,0xba,0,0,0x31,0xc9,0xb4,0x3c,0xcd,0x21,0x89,0xc3,0xba,0,0,0xb9,2,0,0xb4,0x40,0xcd,0x21,0xb4,0x3e,0xcd,0x21,0xeb,0xfe };
            int pointerAddress = 0x100 + memoryProbe.Count, valueAddress = pointerAddress + 4, fileAddress = valueAddress + 2;
            void Address(int offset, int address) { memoryProbe[offset] = (byte)address; memoryProbe[offset + 1] = (byte)(address >> 8); }
            Address(7,pointerAddress); Address(11,pointerAddress+2); Address(17,pointerAddress); Address(21,valueAddress); Address(24,fileAddress); Address(35,valueAddress);
            memoryProbe.AddRange([0, 0, 0, 0, 0, 0]); memoryProbe.AddRange(System.Text.Encoding.ASCII.GetBytes("MEM.BIN\0")); File.WriteAllBytes(Path.Combine(c,"GAMES","MEMCHK.COM"),memoryProbe.ToArray());
            profile.Startup = "C:\\GAMES\\MEMCHK.COM"; Launch(profile);
            string memoryAnswer = Path.Combine(c,"GAMES","MEM.BIN");
            for (int i = 0; i < 100 && (!File.Exists(memoryAnswer) || new FileInfo(memoryAnswer).Length < 2); i++) await Task.Delay(50);
            int freeKb = File.Exists(memoryAnswer) ? BitConverter.ToUInt16(File.ReadAllBytes(memoryAnswer)) : -1;
            report["dos_xms_free_kb"] = freeKb;
            Check(freeKb >= 10 * 1024 && freeKb < 12 * 1024, "DOS XMS query confirms the configured 12 MB machine instead of the default 32 MB");
            profile.Startup = startup.Text = "C:\\GAMES\\VERIFY.COM";
            Stop(); Duplicate(); var copy = selected!; addedProfiles.Add(copy);
            copy.Programs[1].Name = "Copy only"; copy.DosboxSettings["custom"]["preserve"] = "copy";
            Check(copy.Id != profile.Id && copy.Drives[0].Folder == profile.Drives[0].Folder && profile.Programs[1].Name == "My custom launcher" && profile.DosboxSettings["custom"]["preserve"] == "yes", "one-click environment copies have independent configuration and menu, with shared folder mappings");
            environments.SelectedItem = profile; await Scanned();
            copy.Name = "Adventure collection"; UpdateProgramPicker(); programPicker.Open(); await Task.Delay(120);
            Check(programPicker.Tree.Items.OfType<TreeViewItem>().Any(r => Equals(r.Tag, profile.Id)) && programPicker.Tree.Items.OfType<TreeViewItem>().Any(r => Equals(r.Tag, copy.Id)), "program picker shows separate expandable environment groups");
            programPicker.Search.Text = "ADVENTURE";
            Check(programPicker.Tree.Items.Count == 1 && Equals(((TreeViewItem)programPicker.Tree.Items[0]).Tag, copy.Id), "search matches environment names across the library without case sensitivity");
            programPicker.Search.Text = "D:\\MORE\\OTHER";
            Check(programPicker.Tree.Items.OfType<TreeViewItem>().SelectMany(r => r.Items.OfType<TreeViewItem>()).Count() == 2, "DOS path search finds matching programs in both environments");
            programPicker.Search.Text = "no such program 9123"; Check(programPicker.Tree.Items.Count == 0, "unmatched searches show no programs");
            programPicker.Search.Text = "OTHER"; await Task.Delay(80);
            var popup = System.Windows.PresentationSource.FromVisual(programPicker.Search)?.RootVisual as FrameworkElement;
            if (popup != null) CaptureElement(popup, Path.Combine(results, "program-tree.png"));
            var copyLeaf = programPicker.Tree.Items.OfType<TreeViewItem>().Where(r => Equals(r.Tag, copy.Id)).SelectMany(r => r.Items.OfType<TreeViewItem>()).Single();
            ((FrameworkElement)copyLeaf.Header).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); await Task.Delay(120);
            Check(selected == copy && programPicker.SelectedChoice?.Environment == copy && programPicker.SelectedChoice.Program.Path == "D:\\MORE\\OTHER.BAT", "choosing a leaf switches environment and preserves the chosen program");
            Click(menu.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == "Run selected program / launch environment"));
            for (int i = 0; i < 100 && core?.Latest == null; i++) await Task.Delay(50);
            Check(running?.Id == copy.Id && running.Startup == "D:\\MORE\\OTHER.BAT", "Play runs the chosen environment even when DOS paths are identical"); Stop(); await Scanned();
            var renamed = programPicker.Choices.Single(p => p.Environment == copy && p.Program.Path.EndsWith("OTHER.BAT")); RenameProgram(renamed); await Task.Delay(80);
            Descendants<TextBox>(renameProgramWindow!).Single().Text = "A lovely adventure"; Click(Descendants<Button>(renameProgramWindow!).Single(b => Equals(b.Content, "Save name")));
            programPicker.Open(); programPicker.Search.Text = "lovely";
            Check(programPicker.Tree.Items.Count == 1 && renamed.Program.Name == "A lovely adventure" && Library.Load().Environments.Single(p => p.Id == copy.Id).Programs.Any(p => p.Name == "A lovely adventure"), "friendly names saved from the picker are persisted and searchable"); programPicker.Close();
            FocusForValidation(); programPicker.Open(); programPicker.Search.Text = "lovely"; await Task.Delay(100); RefreshPrograms(); await Scanned();
            Check(programPicker.IsOpen && programPicker.Search.Text == "lovely", "background scans preserve an open search and its query");
            programPicker.Search.Focus(); await Task.Delay(50); report["search_focused"] = programPicker.Search.IsKeyboardFocused; report["search_window_active"] = IsActive;
            SendValidationInput(KeyInput(13, true), KeyInput(13, false)); await Task.Delay(100);
            report["enter_picker_open"] = programPicker.IsOpen; report["enter_choice"] = programPicker.SelectedChoice?.Program.Name;
            Check(!programPicker.IsOpen && programPicker.SelectedChoice?.Program.Name == "A lovely adventure", "Enter chooses the first search result through real keyboard input");
            programPicker.Open(); await Task.Delay(100); SendValidationInput(KeyInput(27, true), KeyInput(27, false)); await Task.Delay(100);
            Check(!programPicker.IsOpen, "Escape closes the picker through real keyboard input");
            environments.SelectedItem = profile; await Scanned();
            UpdateLayout(); CaptureWindow(Path.Combine(results, "icon-toolbar.png"));
            ToggleFullscreen(); await Task.Delay(150); Check(!environmentVisible && !effectsVisible, "fullscreen begins with both panels hidden");
            Check(!TestTopPointer(400, 0, 0) && !TestTopPointer(400, 0, .99) && TestTopPointer(400, 0, 1), "fullscreen icon menu still waits one second at the top edge");
            programPicker.Open(); await Task.Delay(1000); Check(fullscreenMenu.IsOpen && programPicker.IsOpen, "fullscreen menu remains open while searching the program tree"); programPicker.Close();
            Click(environmentButton!); await Task.Delay(100); Check(environmentVisible && !effectsVisible && fullscreenMenu.IsOpen, "fullscreen can show only the environment panel");
            CaptureWindow(Path.Combine(results, "fullscreen-icons.png")); ToggleFullscreen();
            report["passed"] = true;
        }
        catch (Exception e) { report["passed"] = false; report["error"] = e.ToString(); }
        finally
        {
            Stop(); programPicker.Close(); renameProgramWindow?.Close(); programManager?.Close(); configurationWindow?.Close(); if (fullscreen) ToggleFullscreen();
            foreach (var added in addedProfiles) library.Environments.Remove(added); library.Save(); environments.SelectedItem = original;
            preferences.EnvironmentVisible = savedEnvironmentVisible; preferences.EffectsVisible = savedEffectsVisible; environmentVisible = savedEnvironmentVisible; effectsVisible = savedEffectsVisible; ApplyLayout(); preferences.Save();
            report["checks"] = checks; Paths.AtomicWrite(Path.Combine(results, "ui-validation.json"), JsonSerializer.Serialize(report, Paths.Json)); Close();
        }
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    Window? configurationWindow;
    static string CoreCatalogPath => Path.Combine(Paths.Data, "core-options.json");
    Dictionary<string, string> CoreCatalog()
    {
        if (core?.OptionDefinitions.Count > 0) return new(core.OptionDefinitions);
        try { return File.Exists(CoreCatalogPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CoreCatalogPath))! : []; }
        catch { return []; }
    }
    void CacheCoreCatalog()
    {
        if (core?.IsReady == true && core.OptionDefinitions.Count > 0 && !coreCatalogStored)
        {
            Paths.AtomicWrite(CoreCatalogPath, JsonSerializer.Serialize(core.OptionDefinitions, Paths.Json)); coreCatalogStored = true;
        }
    }
    bool coreCatalogStored;
    public void ShowDosboxSettings()
    {
        ReleaseMouseCapture();
        if (configurationWindow != null) { configurationWindow.Activate(); return; }
        var profile = selected ?? running ?? throw new InvalidOperationException("Select an environment first.");
        var definitions = ConfigurationCatalog.Settings.ToList();
        var catalog = CoreCatalog();
        foreach (var option in catalog.OrderBy(p => p.Key))
        {
            if (option.Key == "dosbox_pure_conf") continue;
            int separator = option.Value.IndexOf(';'); if (separator < 0) continue;
            var label = option.Value[..separator].Trim();
            definitions.Add(new("Core options", "libretro", option.Key, label, "DOSBox Pure frontend option. Takes effect on the next launch / reload.", option.Value[(separator + 1)..].Trim().Split('|')));
        }
        var draft = DosboxIni.Effective(profile);
        var dialog = configurationWindow = new Window { Owner = this, Title = "DOSBox configuration · " + profile.Name, Width = 1060, Height = 820, MinWidth = 800, MinHeight = 540, Background = Background, Foreground = Foreground, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        header.Children.Add(Label(profile.Name + " · DOSBox configuration", 24));
        header.Children.Add(Label("Saved only to this environment. Blank fields use app / engine defaults. Save & reload restarts DOS; Save applies on the next launch. Core options can override the corresponding DOSBox settings; availability depends on the embedded core."));
        var search = new TextBox { ToolTip = "Find any setting by name, section or description" }; header.Children.Add(search);
        var footer = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var message = Label(""); message.Foreground = Brushes.LightSteelBlue; DockPanel.SetDock(message, Dock.Top); footer.Children.Add(message);
        var buttons = new WrapPanel(); footer.Children.Add(buttons);
        var center = new Grid(); center.ColumnDefinitions.Add(new() { Width = new GridLength(190) }); center.ColumnDefinitions.Add(new()); body.Children.Add(center);
        var navigation = new ListBox { Margin = new Thickness(0, 12, 14, 0) }; center.Children.Add(navigation);
        var categories = definitions.Select(s => s.Group).Distinct().ToList(); if (!categories.Contains("Core options")) categories.Add("Core options"); categories.Add("Advanced INI");
        navigation.ItemsSource = categories;
        var forms = new StackPanel { Margin = new Thickness(6, 0, 10, 0) };
        var mouseOptions = Button("Mouse capture & release shortcut…", ShowMouseSettings); forms.Children.Add(mouseOptions);
        var scroll = new ScrollViewer { Content = forms, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); center.Children.Add(scroll);
        var advanced = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 12, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(advanced, 1); center.Children.Add(advanced);
        var controls = new Dictionary<DosSetting, ComboBox>(); var rows = new Dictionary<DosSetting, StackPanel>();
        foreach (var definition in definitions)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; forms.Children.Add(row); rows[definition] = row;
            row.Children.Add(Label(definition.Name, 16)); row.Children.Add(new TextBlock { Text = definition.Help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 1, 0, 5) });
            var line = new DockPanel(); row.Children.Add(line);
            var reset = IconButton("restart", "Use app / engine default for " + definition.Name, () => controls[definition].Text = ""); DockPanel.SetDock(reset, Dock.Right); line.Children.Add(reset);
            var input = new ComboBox { IsEditable = true, ItemsSource = definition.Choices, ToolTip = "[" + definition.Section + "] " + definition.Key + " · blank = engine default", MinWidth = 170 };
            controls.Add(definition, input); line.Children.Add(input);
            row.Children.Add(new TextBlock { Text = "[" + definition.Section + "] " + definition.Key, Foreground = Brush(103, 137, 153), FontSize = 11 });
        }
        var emptyCore = Label("Launch a DOS environment once to load the full catalog of options exposed by the installed core. Its settings will then be available here even after stopping DOS."); forms.Children.Add(emptyCore);
        string activePage = "CPU & memory"; bool syncing = false;
        void Populate()
        {
            foreach (var item in controls) item.Value.Text = draft.TryGetValue(item.Key.Section, out var section) && section.TryGetValue(item.Key.Key, out var value) ? value : "";
        }
        void Collect(bool validate = true)
        {
            if (activePage == "Advanced INI") { draft = DosboxIni.Parse(advanced.Text); }
            else foreach (var item in controls)
            {
                string value = item.Value.Text.Trim(); if (validate) ConfigurationCatalog.Validate(item.Key, value);
                if (value.Length > 0) DosboxIni.Set(draft, item.Key.Section, item.Key.Key, value);
                else if (draft.TryGetValue(item.Key.Section, out var section)) section.Remove(item.Key.Key);
            }
            if (validate) foreach (var def in definitions) if (draft.TryGetValue(def.Section, out var section) && section.TryGetValue(def.Key, out var value)) ConfigurationCatalog.Validate(def, value);
        }
        void Filter()
        {
            bool raw = activePage == "Advanced INI"; scroll.Visibility = raw ? Visibility.Collapsed : Visibility.Visible; advanced.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in rows)
            {
                bool match = (row.Key.Name + " " + row.Key.Key + " " + row.Key.Section + " " + row.Key.Help).Contains(search.Text, StringComparison.OrdinalIgnoreCase);
                row.Value.Visibility = (search.Text.Length > 0 ? match : row.Key.Group == activePage) ? Visibility.Visible : Visibility.Collapsed;
            }
            emptyCore.Visibility = activePage == "Core options" && catalog.Count == 0 && search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            mouseOptions.Visibility = activePage == "Input" || (search.Text.Length > 0 && "mouse capture release shortcut sensitivity".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        }
        navigation.SelectionChanged += (_, _) =>
        {
            if (syncing || navigation.SelectedItem is not string next) return;
            try { Collect(false); activePage = next; Populate(); if (next == "Advanced INI") advanced.Text = DosboxIni.Format(draft); Filter(); message.Text = ""; }
            catch (Exception e) { message.Text = e.Message; syncing = true; navigation.SelectedItem = activePage; syncing = false; }
        };
        search.TextChanged += (_, _) => Filter();
        void Save(bool reload)
        {
            try { Collect(); StoreDosboxSettings(profile, draft); if (reload) Launch(profile); else _ = ApplyCpuControlsUi(profile); dialog.Close(); }
            catch (Exception e) { message.Text = e.Message; }
        }
        buttons.Children.Add(Button("Save", () => Save(false))); buttons.Children.Add(Button("Save & reload DOS", () => Save(true)));
        buttons.Children.Add(Button("Copy environment", () => { try { Collect(); StoreDosboxSettings(profile, draft); environments.SelectedItem = profile; Duplicate(); dialog.Close(); } catch (Exception e) { message.Text = e.Message; } }));
        buttons.Children.Add(Button("Import…", () =>
        {
            var picker = new OpenFileDialog { Filter = "DOSBox configuration|*.conf;*.ini|All files|*.*" }; if (picker.ShowDialog(dialog) != true) return;
            try { var imported = DosboxIni.Parse(File.ReadAllText(picker.FileName)); draft = imported; Populate(); advanced.Text = DosboxIni.Format(draft); message.Text = "Imported into this draft. Save to apply."; } catch (Exception e) { message.Text = e.Message; }
        }));
        buttons.Children.Add(Button("Export…", () =>
        {
            try { Collect(); var picker = new SaveFileDialog { Filter = "DOSBox configuration|*.conf", FileName = "environment.conf" }; if (picker.ShowDialog(dialog) == true) File.WriteAllText(picker.FileName, DosboxIni.Format(draft)); } catch (Exception e) { message.Text = e.Message; }
        }));
        buttons.Children.Add(Button("Older games preset", () =>
        {
            try { Collect(false); DosboxIni.Set(draft, "cpu", "core", "normal"); DosboxIni.Set(draft, "cpu", "cycles", "3000"); DosboxIni.Set(draft, "dosbox", "memsize", "8"); DosboxIni.Set(draft, "sblaster", "sbtype", "sb16"); Populate(); advanced.Text = DosboxIni.Format(draft); message.Text = "Compatibility preset in draft: normal CPU, 3000 cycles, 8 MB, SB16."; } catch (Exception e) { message.Text = e.Message; }
        }));
        buttons.Children.Add(Button("Cancel", dialog.Close));
        Populate(); navigation.SelectedIndex = 0; dialog.Content = body;
        dialog.Closed += (_, _) => configurationWindow = null; dialog.Show();
    }
}

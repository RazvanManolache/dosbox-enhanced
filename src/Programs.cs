using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

namespace DosboxEnhanced;

public sealed record ProgramScan(List<ProgramEntry> Entries, bool Complete, string Note);
public static class ProgramCatalog
{
    public static ProgramScan Scan(IEnumerable<Drive> drives, CancellationToken cancel = default, Func<Drive,string,bool>? allowPath = null)
    {
        var found = new Dictionary<string, ProgramEntry>(StringComparer.OrdinalIgnoreCase);
        bool complete = true; int directories = 0;
        var limit = DateTime.UtcNow.AddSeconds(15);
        foreach (var drive in drives)
        {
            var root = Path.GetFullPath(drive.Folder);
            string Title(string relative)
            {
                var friendly = drive.DisplayNames.Where(p => relative.StartsWith(p.Key.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Key.Length).FirstOrDefault().Value;
                return friendly == null ? Path.GetFileNameWithoutExtension(relative) : friendly + " / " + Path.GetFileName(relative);
            }
            void Add(string relative)
            {
                if(allowPath!=null && !allowPath(drive,relative)) {complete=false;return;}
                string path = drive.Letter.ToUpperInvariant() + ":\\" + relative;
                found.TryAdd(path, new() { Path = path, Name = Title(relative), Available = true });
            }
            // The curated game launchers precede utilities on the first scan.
            foreach (var launcher in drive.Launchers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.Combine(launcher.Key, launcher.Value);
                if (File.Exists(Path.Combine(root, relative))) Add(relative);
            }
            var queue = new Stack<(string Folder, HashSet<string> Ancestors, int Depth)>();
            queue.Push((root, new(StringComparer.OrdinalIgnoreCase), 0));
            while (queue.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (++directories > 20000 || found.Count >= 20000 || DateTime.UtcNow > limit) { complete = false; break; }
                var (folder, ancestors, depth) = queue.Pop();
                if(allowPath!=null && !allowPath(drive,folder.Equals(root,StringComparison.OrdinalIgnoreCase)?"":Path.GetRelativePath(root,folder))) {complete=false;continue;}
                if (depth > 64) { complete = false; continue; }
                try
                {
                    var info = new DirectoryInfo(folder);
                    var physical = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
                    if (!ancestors.Add(Path.TrimEndingDirectorySeparator(physical))) continue;
                    foreach (var file in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".com", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) Add(Path.GetRelativePath(root, file.FullName));
                    }
                    foreach (var child in info.EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        queue.Push((child.FullName, new(ancestors, StringComparer.OrdinalIgnoreCase), depth + 1));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
            }
        }
        return new(found.Values.ToList(), complete, complete ? $"{found.Count} programs found across all mapped drives." : $"{found.Count} programs found. Some folders were inaccessible or the scan limit was reached; try scanning again.");
    }
    public static bool Merge(List<ProgramEntry> entries, ProgramScan scan, bool addNew)
    {
        bool changed = false;
        var existing = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var available = scan.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) entry.Available = available.Contains(entry.Path) || (!scan.Complete && entry.Available);
        foreach (var entry in scan.Entries)
            if (addNew && !existing.ContainsKey(entry.Path)) { entries.Add(entry); existing.Add(entry.Path, entry); changed = true; }
        return changed;
    }
}
public sealed partial class MainWindow
{
    readonly DispatcherTimer programTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    CancellationTokenSource? scanCancellation;
    bool scanningPrograms, managerOpen;
    Window? programManager;
    Action<EnvironmentProfile, ProgramScan>? updateOpenManager;
    string programScanNote = "Scanning mapped folders…";
    string? pickerEnvironmentId;
    string pickerPath = "";
    Window? renameProgramWindow;
    void InitializePrograms() { Loaded += (_, _) => RefreshPrograms(); programTimer.Tick += (_, _) => { if (!managerOpen && !scanningPrograms && library.Environments.Any(p => p.DiscoverPrograms)) RefreshPrograms(); }; programTimer.Start(); }
    async void RefreshPrograms()
    {
        UpdateProgramPicker();
        if (!IsLoaded) return;
        scanCancellation?.Cancel();
        var cancellation = scanCancellation = new(); scanningPrograms = true;
        var snapshots = library.Environments.Select(p => (Profile: p, Mappings: JsonSerializer.Serialize(p.Drives))).ToList();
        try
        {
            var scans = await Task.Run(() => snapshots.Select(s => (s.Profile, s.Mappings, Scan: ProgramCatalog.Scan(JsonSerializer.Deserialize<List<Drive>>(s.Mappings)!, cancellation.Token))).ToList());
            if (disposed || cancellation.IsCancellationRequested) return;
            bool changed = false;
            foreach (var (profile, mappings, scan) in scans)
            {
                // Ignore results if a drive changed or the environment was removed mid-scan.
                if (!library.Environments.Contains(profile) || JsonSerializer.Serialize(profile.Drives) != mappings) continue;
                changed |= ProgramCatalog.Merge(profile.Programs, scan, profile.DiscoverPrograms);
                updateOpenManager?.Invoke(profile, scan);
                if (ReferenceEquals(selected, profile)) programScanNote = scan.Note;
            }
            if (changed) library.Save();
            UpdateProgramPicker();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { programScanNote = "Program scan: " + e.Message; programPicker.ToolTip = programScanNote; }
        finally { if (ReferenceEquals(scanCancellation, cancellation)) { scanningPrograms = false; scanCancellation = null; } cancellation.Dispose(); }
    }
    void UpdateProgramPicker()
    {
        programPicker.Refresh(library.Environments, pickerEnvironmentId, pickerPath);
    }
    void RenameProgram(ProgramChoice choice)
    {
        ReleaseMouseCapture();
        if (renameProgramWindow != null) { renameProgramWindow.Activate(); return; }
        var dialog = renameProgramWindow = new Window { Title = "Rename program · " + choice.Environment.Name, Owner = this, Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Background = Background, Foreground = Foreground, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(20) }; body.Children.Add(Label("Program display name", 20)); body.Children.Add(Label(choice.Program.Path));
        var input = new TextBox { Text = choice.Program.Name, MaxLength = 160 }; body.Children.Add(input);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var save = Button("Save name", () => { if (!library.Environments.Contains(choice.Environment) || !choice.Environment.Programs.Contains(choice.Program)) { dialog.Close(); return; } choice.Program.Name = FriendlyName(input.Text); library.Save(); UpdateProgramPicker(); dialog.Close(); }); save.IsDefault = true;
        buttons.Children.Add(save); var cancel = Button("Cancel", dialog.Close); cancel.IsCancel = true; buttons.Children.Add(cancel); body.Children.Add(buttons); dialog.Content = body;
        dialog.Closed += (_, _) => renameProgramWindow = null; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; dialog.Show();
    }
    public void ShowProgramManager()
    {
        ReleaseMouseCapture();
        if (programManager != null) { programManager.Activate(); return; }
        var profile = selected ?? throw new InvalidOperationException("Select an environment first.");
        managerOpen = true;
        var working = profile.Programs.Select(p => new ProgramEntry { Path = p.Path, Name = p.Name, Visible = p.Visible, Available = p.Available }).ToList();
        var dialog = programManager = new Window { Owner = this, Title = "Programs · " + profile.Name, Width = 880, Height = 720, MinWidth = 660, MinHeight = 480, Background = Background, Foreground = Foreground, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
        top.Children.Add(Label("Your program menu", 24)); top.Children.Add(Label("Programs from every mapped drive and folder. Uncheck to hide; hidden entries stay hidden after rescanning. Your order is kept, and new programs go at the end."));
        var auto = new CheckBox { Content = "Automatically add newly discovered EXE, COM and BAT files", IsChecked = profile.DiscoverPrograms }; top.Children.Add(auto);
        var search = new TextBox { ToolTip = "Filter by name or DOS path" }; top.Children.Add(Label("Find a program")); top.Children.Add(search);
        var note = Label(programScanNote); top.Children.Add(note);
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Render(string? selectedPath = null)
        {
            list.Items.Clear();
            foreach (var entry in working.Where(p => (p.Name + " " + p.Path).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var text = new StackPanel(); text.Children.Add(Label(entry.Name, 15)); text.Children.Add(new TextBlock { Text = entry.Path + (entry.Available ? "" : "   ·   unavailable"), Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 8) });
                var check = new CheckBox { Content = text, IsChecked = entry.Visible, ToolTip = "Show in program menu", HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0) };
                check.Checked += (_, _) => entry.Visible = true; check.Unchecked += (_, _) => entry.Visible = false;
                var item = new ListBoxItem { Content = check, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch }; list.Items.Add(item); if (entry.Path == selectedPath) list.SelectedItem = item;
                check.PreviewMouseLeftButtonDown += (_, _) => item.IsSelected = true;
            }
        }
        ProgramEntry? Current() => (list.SelectedItem as ListBoxItem)?.Tag as ProgramEntry;
        updateOpenManager = (scannedProfile, scan) => { if (!ReferenceEquals(profile, scannedProfile)) return; var path = Current()?.Path; ProgramCatalog.Merge(working, scan, auto.IsChecked == true); note.Text = scan.Note; Render(path); };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        void Move(int delta) { if (Current() is not ProgramEntry entry) return; int index = working.IndexOf(entry), next = index + delta; if (next < 0 || next >= working.Count) return; working.RemoveAt(index); working.Insert(next, entry); Render(entry.Path); }
        actions.Children.Add(Button("Move up", () => Move(-1))); actions.Children.Add(Button("Move down", () => Move(1)));
        var rename = new TextBox { Width = 210, ToolTip = "Display name for selected program" }; actions.Children.Add(rename);
        list.SelectionChanged += (_, _) => rename.Text = Current()?.Name ?? "";
        actions.Children.Add(Button("Rename", () => { if (Current() is ProgramEntry entry && !string.IsNullOrWhiteSpace(rename.Text)) { entry.Name = rename.Text.Trim(); Render(entry.Path); } }));
        actions.Children.Add(Button("Show all", () => { working.ForEach(p => p.Visible = true); Render(); })); actions.Children.Add(Button("Hide all", () => { working.ForEach(p => p.Visible = false); Render(); }));
        var scanButton = Button("Scan folders now", () => { }); actions.Children.Add(scanButton);
        scanButton.Click += async (_, _) =>
        {
            scanButton.IsEnabled = false; note.Text = "Scanning mapped folders…";
            try { var maps = JsonSerializer.Deserialize<List<Drive>>(JsonSerializer.Serialize(profile.Drives))!; var scan = await Task.Run(() => ProgramCatalog.Scan(maps)); ProgramCatalog.Merge(working, scan, true); note.Text = scan.Note; Render(); }
            catch (Exception e) { note.Text = e.Message; } finally { scanButton.IsEnabled = true; }
        };
        var footer = new WrapPanel(); bottom.Children.Add(footer);
        footer.Children.Add(Button("Save program menu", () => { profile.Programs = working; profile.DiscoverPrograms = auto.IsChecked == true; library.Save(); UpdateProgramPicker(); dialog.Close(); }));
        footer.Children.Add(Button("Cancel", dialog.Close));
        search.TextChanged += (_, _) => Render(); body.Children.Add(list); dialog.Content = body; Render();
        dialog.Closed += (_, _) => { managerOpen = false; programManager = null; updateOpenManager = null; }; dialog.Show();
    }
}

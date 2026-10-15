using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;

namespace DosboxEnhanced;

public sealed record ProgramChoice(EnvironmentProfile Environment, ProgramEntry Program);

// A real tree rather than a flat ComboBox: environment headings never launch DOS.
public sealed class ProgramPicker : UserControl
{
    readonly Button button = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 2, 10, 2) };
    readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
    readonly TextBlock subtitle = new() { FontSize = 10, Foreground = Brushes.LightSlateGray, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Popup popup = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
    internal readonly TextBox Search = new() { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Search environments, program names or DOS paths" };
    internal readonly TreeView Tree = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.WhiteSmoke, MaxHeight = 440 };
    readonly TextBlock empty = new() { Text = "No matching programs", Margin = new Thickness(10), Foreground = Brushes.LightSlateGray };
    List<EnvironmentProfile> profiles = [];
    readonly HashSet<string> expanded = [];
    ProgramChoice? choice;
    bool rendering;
    string signature = "";
    public ProgramChoice? SelectedChoice => choice;
    public IReadOnlyList<ProgramChoice> Choices => profiles.SelectMany(p => Entries(p).Select(e => new ProgramChoice(p, e))).ToList();
    public bool IsOpen => popup.IsOpen;
    public event Action<ProgramChoice>? Chosen;
    public event Action<ProgramChoice>? RenameRequested;
    public event Action? Opening;
    static IEnumerable<ProgramEntry> Entries(EnvironmentProfile p) => new[] { new ProgramEntry { Name = "DOS prompt", Available = true } }.Concat(p.Programs.Where(e => e.Visible && e.Available));
    static bool Same(ProgramChoice? a, ProgramChoice? b) => a?.Environment.Id == b?.Environment.Id && string.Equals(a?.Program.Path, b?.Program.Path, StringComparison.OrdinalIgnoreCase);
    public ProgramPicker()
    {
        Tree.Resources[typeof(TreeViewItem)] = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TreeViewItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TreeViewItem">
                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                  <ToggleButton x:Name="Expander" Width="18" Height="24" Focusable="False" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}">
                    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><Path x:Name="Arrow" Data="M 0,0 L 4,4 L 0,8" Stroke="#A8C7D9" StrokeThickness="1.5" Width="5" Height="9" VerticalAlignment="Center" HorizontalAlignment="Center" RenderTransformOrigin="0.5,0.5"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
                  </ToggleButton>
                  <Border x:Name="Selection" Grid.Column="1" Background="Transparent" CornerRadius="3" Padding="2"><ContentPresenter ContentSource="Header" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/></Border>
                  <ItemsPresenter x:Name="Children" Grid.Row="1" Grid.Column="1"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsExpanded" Value="False"><Setter TargetName="Children" Property="Visibility" Value="Collapsed"/></Trigger>
                  <Trigger Property="HasItems" Value="False"><Setter TargetName="Expander" Property="Visibility" Value="Hidden"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Selection" Property="Background" Value="#254F64"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel(); labels.Children.Add(title); labels.Children.Add(subtitle); heading.Children.Add(labels);
        var arrow = new TextBlock { Text = "▾", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.LightSteelBlue }; Grid.SetColumn(arrow, 1); heading.Children.Add(arrow);
        button.Content = heading; Content = button;
        System.Windows.Automation.AutomationProperties.SetName(button, "Choose a program from any environment");
        var panel = new DockPanel { Margin = new Thickness(12), Width = 450 };
        var label = new TextBlock { Text = "Find a program", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 0, 0, 5) }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        DockPanel.SetDock(Search, Dock.Top); panel.Children.Add(Search);
        var hint = new TextBlock { Text = "Choose a program, then press Play. Right-click a program to rename it.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(hint, Dock.Bottom); panel.Children.Add(hint);
        var content = new Grid(); content.Children.Add(Tree); content.Children.Add(empty); panel.Children.Add(content);
        popup.Child = new Border { Background = new SolidColorBrush(Color.FromRgb(21, 29, 40)), BorderBrush = new SolidColorBrush(Color.FromRgb(67, 94, 113)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
        popup.PlacementTarget = button;
        button.Click += (_, _) => { if (popup.IsOpen) Close(); else Open(); };
        button.PreviewKeyDown += (_, e) => { if (e.Key is Key.Down or Key.F4) { Open(); e.Handled = true; } };
        Search.TextChanged += (_, _) => Render();
        Search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); button.Focus(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Enter)
            {
                var leaf = Tree.Items.OfType<TreeViewItem>().SelectMany(r => r.Items.OfType<TreeViewItem>()).FirstOrDefault();
                if (leaf != null) { if (e.Key == Key.Enter) Choose((ProgramChoice)leaf.Tag); else { ((TreeViewItem)leaf.Parent).IsExpanded = true; leaf.IsSelected = true; leaf.Focus(); } }
                e.Handled = true;
            }
        };
        Tree.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); button.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && Tree.SelectedItem is TreeViewItem { Tag: ProgramChoice entry }) { Choose(entry); e.Handled = true; }
        };
    }
    public void Open()
    {
        ((FrameworkElement)popup.Child).Resources = Resources;
        Opening?.Invoke(); Search.Text = ""; Render(); popup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Search.Focus()));
    }
    public void Close() => popup.IsOpen = false;
    public void Choose(ProgramChoice value) { Select(value.Environment.Id, value.Program.Path); Close(); Chosen?.Invoke(value); }
    public void Select(string? environmentId, string path)
    {
        choice = Choices.FirstOrDefault(c => c.Environment.Id == environmentId && c.Program.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? Choices.FirstOrDefault(c => c.Environment.Id == environmentId);
        title.Text = choice?.Program.Name ?? "Choose a program"; subtitle.Text = choice?.Environment.Name ?? "All environments";
        button.ToolTip = choice == null ? "Browse programs across all environments" : $"{choice.Environment.Name}\n{choice.Program.Path}\nChoose a program, then press Play";
    }
    public void Refresh(IEnumerable<EnvironmentProfile> environments, string? environmentId, string path)
    {
        profiles = environments.ToList(); Select(environmentId, path);
        // Leave the search caret and tree alone during background scans with no changes.
        string next = string.Join("\n", Choices.Select(c => $"{c.Environment.Id}|{c.Environment.Name}|{c.Program.Path}|{c.Program.Name}"));
        if (next != signature) { signature = next; Render(); }
    }
    void Render()
    {
        rendering = true; Tree.Items.Clear(); int count = 0;
        foreach (var p in profiles)
        {
            var matches = Entries(p).Where(e => (p.Name + " " + e.Name + " " + e.Path).Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) continue;
            var root = new TreeViewItem { Header = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(2, 7, 0, 7) }, Tag = p.Id, IsExpanded = Search.Text.Length > 0 || expanded.Contains(p.Id) || choice?.Environment.Id == p.Id };
            root.Expanded += (_, e) => { if (!rendering && ReferenceEquals(e.OriginalSource, root)) expanded.Add(p.Id); };
            root.Collapsed += (_, e) => { if (!rendering && ReferenceEquals(e.OriginalSource, root)) expanded.Remove(p.Id); };
            foreach (var entry in matches)
            {
                var value = new ProgramChoice(p, entry); var text = new StackPanel { Margin = new Thickness(4, 4, 10, 4), Background = Brushes.Transparent };
                text.Children.Add(new TextBlock { Text = entry.Name, Foreground = Brushes.WhiteSmoke, TextTrimming = TextTrimming.CharacterEllipsis });
                if (entry.Path.Length > 0) text.Children.Add(new TextBlock { Text = entry.Path, Foreground = Brushes.LightSlateGray, FontSize = 11 });
                var leaf = new TreeViewItem { Header = text, Tag = value, IsSelected = Same(choice, value), ToolTip = entry.Path.Length == 0 ? "Open the DOS prompt" : entry.Path };
                text.MouseLeftButtonUp += (_, e) => { Choose(value); e.Handled = true; };
                if (entry.Path.Length > 0)
                {
                    var context = new ContextMenu(); var rename = new MenuItem { Header = "Rename program…" };
                    rename.Click += (_, _) => { Close(); RenameRequested?.Invoke(value); }; context.Items.Add(rename); leaf.ContextMenu = context;
                }
                root.Items.Add(leaf); count++;
            }
            Tree.Items.Add(root);
        }
        empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed; rendering = false;
    }
}

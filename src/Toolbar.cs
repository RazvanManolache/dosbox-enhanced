using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    bool environmentVisible, effectsVisible;
    Button? environmentButton, effectsButton;
    readonly ProgramPicker programPicker = new() { Width = 330, Height = 40, Margin = new Thickness(8, 3, 3, 3) };
    // One consistent vector icon set; no font or external image dependency.
    static readonly Dictionary<string, string> Icons = new()
    {
        ["folder"] = "M2,6 L9,6 L11,9 L22,9 L22,20 L2,20 Z M2,9 L22,9",
        ["play"] = "M7,3 L21,12 L7,21 Z",
        ["pause"] = "M7,4 L7,20 M17,4 L17,20",
        ["save"] = "M3,3 L18,3 L21,6 L21,21 L3,21 Z M7,3 L7,10 L17,10 L17,3 M7,21 L7,14 L17,14 L17,21",
        ["load"] = "M4,4 L4,20 L20,20 L20,4 M12,2 L12,14 M7,9 L12,14 L17,9",
        ["restart"] = "M4,9 A9,9 0 1 1 4,16 M4,3 L4,9 L10,9",
        ["stop"] = "M5,5 L19,5 L19,19 L5,19 Z",
        ["camera"] = "M3,7 L8,7 L10,4 L16,4 L18,7 L22,7 L22,20 L3,20 Z M17,13 A4,4 0 1 1 9,13 A4,4 0 1 1 17,13",
        ["fullscreen"] = "M9,3 L3,3 L3,9 M15,3 L21,3 L21,9 M3,15 L3,21 L9,21 M15,21 L21,21 L21,15",
        ["environment"] = "M2,3 L22,3 L22,21 L2,21 Z M9,3 L9,21 M5,7 L6,7 M5,11 L6,11 M5,15 L6,15",
        ["beautify"] = "M12,2 L14.7,9.3 L22,12 L14.7,14.7 L12,22 L9.3,14.7 L2,12 L9.3,9.3 Z M21,1 L21,5 M19,3 L23,3",
        ["manage"] = "M3,5 L21,5 M3,12 L21,12 M3,19 L21,19 M8,2 L8,8 M16,9 L16,15 M10,16 L10,22",
        ["copy"] = "M8,8 L22,8 L22,22 L8,22 Z M16,5 L16,2 L2,2 L2,16 L5,16",
        ["settings"] = "M12,7 A5,5 0 1 1 12,17 A5,5 0 1 1 12,7 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
        ["volume"] = "M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M16,8 Q20,12 16,16 M19,4 Q26,12 19,20",
        ["mcp"] = "M7,3 L7,9 M17,3 L17,9 M4,9 L20,9 L20,13 Q20,18 12,18 Q4,18 4,13 Z M12,18 L12,23",
        ["mouse"] = "M12,2 Q5,2 5,9 L5,15 Q5,22 12,22 Q19,22 19,15 L19,9 Q19,2 12,2 Z M12,2 L12,10 M5,10 L19,10"
    };
    static Viewbox ToolbarIcon(string name) => new() { Width = 21, Height = 21, Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(Icons[name]), Stroke = Brush(180, 218, 231), StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 26, Height = 24 } };
    Button IconButton(string icon, string tooltip, Action action)
    {
        var button = Button("", action); button.Content = ToolbarIcon(icon); button.Width = 38; button.Height = 36; button.Padding = new Thickness(7); button.ToolTip = tooltip;
        AutomationProperties.SetName(button, tooltip); ToolTipService.SetInitialShowDelay(button, 250);
        return button;
    }
    void BuildToolbar()
    {
        environmentButton = IconButton("environment", "Show / hide environment", () => TogglePane(true)); menu.Children.Add(environmentButton);
        menu.Children.Add(IconButton("folder", "Open a DOS EXE, COM or BAT", OpenQuick));
        programPicker.Resources = Resources;
        programPicker.Opening += () => { ReleaseMouseCapture(); core?.ReleaseKeys(); };
        programPicker.Chosen += choice => { environments.SelectedItem = choice.Environment; pickerEnvironmentId = choice.Environment.Id; pickerPath = choice.Program.Path; programPicker.Select(pickerEnvironmentId, pickerPath); };
        programPicker.RenameRequested += RenameProgram;
        menu.Children.Add(programPicker);
        menu.Children.Add(IconButton("play", "Run selected program / launch environment", () => { if (programPicker.SelectedChoice is { } choice) { environments.SelectedItem = choice.Environment; startup.Text = choice.Program.Path; } LaunchSelected(); }));
        menu.Children.Add(IconButton("manage", "Manage programs: show, hide, rename and reorder", ShowProgramManager));
        menu.Children.Add(IconButton("pause", "Pause / resume", () => core?.Pause(!core.IsPaused)));
        menu.Children.Add(IconButton("save", "Save state", () => _ = State(true))); menu.Children.Add(IconButton("load", "Load state", () => _ = State(false)));
        menu.Children.Add(IconButton("restart", "Restart DOS", () => { core?.Reset(); phoenix.Reset(); })); menu.Children.Add(IconButton("stop", "Stop", Stop));
        menu.Children.Add(IconButton("camera", "Screenshot", Screenshot)); menu.Children.Add(IconButton("fullscreen", "Fullscreen · F11", ToggleFullscreen));
        effectsButton = IconButton("beautify", "Beautify · show / hide effects", () => TogglePane(false)); menu.Children.Add(effectsButton);
        mouseButton = IconButton("mouse", "Mouse capture settings", ShowMouseSettings); menu.Children.Add(mouseButton);
        muted.Content = ToolbarIcon("volume"); muted.ToolTip = "Mute"; muted.Margin = new Thickness(8, 0, 2, 0); AutomationProperties.SetName(muted, "Mute audio"); menu.Children.Add(muted);
        volume.Width = 76; volume.Margin = new Thickness(0, 0, 6, 0); AutomationProperties.SetName(volume, "Volume"); menu.Children.Add(volume);
        mcpEnabled.Content = null; mcpEnabled.Margin = new Thickness(3, 0, 0, 0); AutomationProperties.SetName(mcpEnabled, "Enable AI connection"); menu.Children.Add(mcpEnabled);
        menu.Children.Add(IconButton("mcp", "AI / MCP connection options", ShowMcpPanel));
        menu.SizeChanged += (_, _) => { if (fullscreen && panels) deck.Margin = new Thickness(0, menu.ActualHeight, 0, 0); };
    }
    void TogglePane(bool environment)
    {
        ReleaseMouseCapture();
        if (environment) environmentVisible = !environmentVisible; else effectsVisible = !effectsVisible;
        SavePanePreferences(); ApplyLayout(); core?.ReleaseKeys();
    }
    void SetAllPanels(bool visible) { ReleaseMouseCapture(); environmentVisible = effectsVisible = visible; SavePanePreferences(); ApplyLayout(); }
    void SavePanePreferences()
    {
        if (fullscreen) return;
        preferences.EnvironmentVisible = environmentVisible; preferences.EffectsVisible = effectsVisible;
        preferencesTimer.Stop(); preferencesTimer.Start();
    }
    void UpdatePaneButtons()
    {
        if (environmentButton != null) { environmentButton.Background = environmentVisible ? Brush(40, 77, 88) : Brush(30, 40, 54); environmentButton.ToolTip = environmentVisible ? "Hide environment" : "Show environment"; }
        if (effectsButton != null) { effectsButton.Background = effectsVisible ? Brush(40, 77, 88) : Brush(30, 40, 54); effectsButton.ToolTip = effectsVisible ? "Beautify · hide effects" : "Beautify · show effects"; }
    }
}

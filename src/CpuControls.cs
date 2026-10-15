using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DosboxEnhanced;

public static class CpuCycles
{
    public static bool IsSimple(string value) => value is "auto" or "max" || int.TryParse(value, out int n) && n > 0;
    public static string Effective(EnvironmentProfile profile) => DosboxIni.Effective(profile)["cpu"]["cycles"];
    public static string ManualValue(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text == "max") return text;
        if (!int.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 2000000)
            throw new ArgumentException("Enter 1–2,000,000 cycles, or max. Advanced syntax stays in DOSBox configuration.");
        return value.ToString(CultureInfo.InvariantCulture);
    }
    public static void Store(EnvironmentProfile profile, string value)
    {
        profile.Cycles = value;
        if (int.TryParse(value, out int n)) profile.ManualCycles = n;
        if (profile.DosboxSettings.TryGetValue("cpu", out var cpu)) cpu.Remove("cycles");
        if (profile.DosboxSettings.TryGetValue("libretro", out var frontend))
            foreach (string key in new[] { "dosbox_pure_cycles", "dosbox_pure_cycles_scale", "dosbox_pure_cycles_max" }) frontend.Remove(key);
    }
}

public sealed partial class MainWindow
{
    readonly CheckBox autoCycles = new() { Content = "Auto", IsChecked = true };
    readonly Slider cycleSpeed = new() { Minimum = 2, Maximum = Math.Log10(2000000), Value = 4, SmallChange = .015, LargeChange = .15 };
    readonly TextBlock cpuHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush(157, 180, 204), Margin = new Thickness(0, 4, 0, 4) };
    readonly DispatcherTimer cpuTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    readonly StackPanel manualCpuControls = new();
    bool syncingCpu, cpuDirty;

    FrameworkElement CpuControls()
    {
        var panel = new StackPanel(); panel.Children.Add(autoCycles); panel.Children.Add(manualCpuControls); manualCpuControls.Children.Add(cycleSpeed);
        var limits = new DockPanel(); limits.Children.Add(new TextBlock { Text = "Slower", FontSize = 11, Foreground = Brush(157, 180, 204) });
        var fast = new TextBlock { Text = "Faster", FontSize = 11, Foreground = Brush(157, 180, 204), HorizontalAlignment = HorizontalAlignment.Right }; limits.Children.Add(fast); manualCpuControls.Children.Add(limits);
        manualCpuControls.Children.Add(cycles); panel.Children.Add(cpuHint);
        cycles.ToolTip = "Exact cycles per millisecond, or max. Enter applies; Auto remembers the last manual value.";
        cycleSpeed.ToolTip = "Drag to adjust CPU cycles. Changes apply after you release the slider.";
        void AutoChanged()
        {
            if (changing || syncingCpu) return;
            syncingCpu = true;
            cycles.Text = Math.Clamp(selected?.ManualCycles ?? 10000, 1, 2000000).ToString(CultureInfo.InvariantCulture);
            cycleSpeed.IsEnabled = cycles.IsEnabled = autoCycles.IsChecked != true;
            manualCpuControls.Visibility = autoCycles.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            cycleSpeed.Value = Math.Log10(Math.Max(100, selected?.ManualCycles ?? 10000));
            syncingCpu = false; cpuDirty = true; CommitCpuControls();
        }
        autoCycles.Checked += (_, _) => AutoChanged(); autoCycles.Unchecked += (_, _) => AutoChanged();
        cycleSpeed.ValueChanged += (_, _) =>
        {
            if (changing || syncingCpu) return;
            double raw = Math.Pow(10, cycleSpeed.Value), step = Math.Pow(10, Math.Max(0, Math.Floor(Math.Log10(raw)) - 2));
            int value = Math.Clamp((int)(Math.Round(raw / step) * step), 100, 2000000);
            syncingCpu = true; cycles.Text = value.ToString(CultureInfo.InvariantCulture); syncingCpu = false;
            cpuDirty = true; cpuTimer.Stop(); cpuTimer.Start();
        };
        cycles.TextChanged += (_, _) => { if (!changing && !syncingCpu) cpuDirty = true; };
        cycles.LostKeyboardFocus += (_, _) => CommitCpuControls();
        cycles.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitCpuControls(); e.Handled = true; } };
        cpuTimer.Tick += (_, _) => { cpuTimer.Stop(); CommitCpuControls(); };
        Closed += (_, _) => cpuTimer.Stop();
        return panel;
    }
    void LoadCpuControls(EnvironmentProfile? profile)
    {
        cpuTimer.Stop(); syncingCpu = true;
        string value = profile == null ? "auto" : CpuCycles.Effective(profile);
        autoCycles.IsChecked = value == "auto";
        cycles.Text = value == "auto" ? (profile?.ManualCycles ?? 10000).ToString(CultureInfo.InvariantCulture) : value;
        int manual = value == "max" ? 2000000 : int.TryParse(cycles.Text, out int n) ? n : profile?.ManualCycles ?? 10000;
        if (profile != null && int.TryParse(value, out int lastManual) && lastManual > 0) profile.ManualCycles = lastManual;
        cycleSpeed.Value = Math.Log10(Math.Clamp(manual, 100, 2000000));
        cycleSpeed.IsEnabled = cycles.IsEnabled = value != "auto";
        manualCpuControls.Visibility = value == "auto" ? Visibility.Collapsed : Visibility.Visible;
        cpuHint.Text = value == "auto" ? "Automatic CPU speed. Uncheck Auto to adjust." : CpuCycles.IsSimple(value) ? "Drag or enter an exact value. Changes apply live." : "Advanced CPU setting. Choose Auto or drag to replace it.";
        syncingCpu = false; cpuDirty = false;
    }
    void CommitCpuControls()
    {
        if (changing || syncingCpu || !cpuDirty || selected == null) return;
        cpuTimer.Stop();
        try
        {
            var profile = selected; string value = autoCycles.IsChecked == true ? "auto" : CpuCycles.ManualValue(cycles.Text);
            CpuCycles.Store(profile, value); library.Save(); LoadCpuControls(profile);
            _ = ApplyCpuControlsUi(profile);
        }
        catch (Exception e) { cpuHint.Text = e.Message; }
    }
    async Task ApplyCpuControlsUi(EnvironmentProfile profile)
    {
        try
        {
            bool applied = await ApplyCpuCycles(profile);
            if (ReferenceEquals(selected, profile)) cpuHint.Text = applied ? "Applied live · saved for this environment" : running?.Id == profile.Id ? "Saved · reload DOS to replace the advanced CPU setting" : "Saved · used on the next launch";
        }
        catch (Exception e) { if (ReferenceEquals(selected, profile)) cpuHint.Text = "Saved · " + e.Message; }
    }
    internal async Task<bool> ApplyCpuCycles(EnvironmentProfile profile)
    {
        var active = core; string value = CpuCycles.Effective(profile);
        if (running?.Id != profile.Id || active?.IsReady != true || !active.LiveCpuCycles || !CpuCycles.IsSimple(value)) return false;
        await active.SetCpuCycles(value);
        if (ReferenceEquals(core, active))
        {
            runningConfig = Regex.Replace(runningConfig, @"(?ms)(^\[cpu\]\r?\n)(.*?)(?=^\[|\z)", match => match.Groups[1].Value + Regex.Replace(match.Groups[2].Value, @"(?m)^cycles=[^\r\n]*", "cycles=" + value));
        }
        return true;
    }
}

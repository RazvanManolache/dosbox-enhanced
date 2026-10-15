using System.IO;
using System.Text.Json;
namespace DosboxEnhanced;
public sealed partial class MainWindow
{
    public async Task RunCpuValidation()
    {
        var original = selected; EnvironmentProfile? profile = null; var checks = new List<string>(); var report = new Dictionary<string, object?>();
        void Check(bool value, string description) { if (!value) throw new Exception(description); checks.Add(description); }
        var output = Path.Combine(Paths.Root, "tests", "results"); Directory.CreateDirectory(output);
        try
        {
            string fixture = Path.Combine(Paths.Root, "tests", "fixtures", "cpu-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            File.WriteAllBytes(Path.Combine(fixture, "WAIT.COM"), [0xb4, 0x00, 0xcd, 0x16, 0xcd, 0x20]);
            File.WriteAllText(Path.Combine(fixture, "CPU.BAT"), "@echo off\r\n" + string.Join("", Enumerable.Range(0, 5).Select(i => $"config -get \"cpu cycles\" > C:\\CPU{i}.TXT\r\nWAIT.COM\r\n")) + ":hold\r\nWAIT.COM\r\ngoto hold\r\n");
            profile = new() { Id = "cpu-test-" + Guid.NewGuid().ToString("N"), Name = "CPU control validation", Cycles = "10000", Startup = "C:\\CPU.BAT", Drives = [new() { Folder = fixture }], Effects = new(Enabled: false, NvidiaUpscaling: false, NvidiaFrameGeneration: false) };
            library.Environments.Add(profile); environments.SelectedItem = profile; Launch(profile); var active = core!;
            async Task<string> Read(int index)
            {
                string file = Path.Combine(fixture, $"CPU{index}.TXT");
                for (int i = 0; i < 200; i++) { if (File.Exists(file) && new FileInfo(file).Length > 0) { await Task.Delay(80); return File.ReadAllText(file).Trim(); } if (active.Error != null) throw new Exception(active.Error); await Task.Delay(50); }
                throw new TimeoutException("DOS did not report CPU cycles at checkpoint " + index);
            }
            async Task Advance(string requested)
            {
                for (int i = 0; i < 100 && active.RequestedCpuCycles != requested; i++) await Task.Delay(30);
                await Task.Delay(200); active.Key(13, true); await Task.Delay(80); active.Key(13, false);
            }
            string initial = await Read(0); report["initial"] = initial;
            Check(initial == "10000" && active.LiveCpuCycles, "initial manual cycles reach the actual DOSBox CPU configuration");
            Check(autoCycles.IsChecked == false && cycles.IsEnabled && cycleSpeed.IsEnabled, "manual mode enables slider and exact value");
            cycles.Text = "12345"; CommitCpuControls(); await Advance("12345"); string fixedValue = await Read(1); report["fixed"] = fixedValue;
            Check(fixedValue == "12345" && ReferenceEquals(core, active), "exact CPU value applies to the running DOS machine without restarting");
            autoCycles.IsChecked = true; await Advance("auto"); string automatic = await Read(2); report["auto"] = automatic;
            Check(automatic == "auto" && !cycles.IsEnabled && !cycleSpeed.IsEnabled && ReferenceEquals(core, active), "Auto applies live and disables manual controls");
            var saved = Library.Load().Environments.Single(p => p.Id == profile.Id);
            Check(saved.Cycles == "auto" && saved.ManualCycles == 12345, "Auto preserves the last manual speed on disk");
            autoCycles.IsChecked = false; await Advance("12345"); string restored = await Read(3);
            Check(restored == "12345" && cycles.Text == "12345", "turning Auto off restores the previous manual speed live");
            cycleSpeed.Value = Math.Log10(20000); CommitCpuControls(); await Advance("20000"); string dragged = await Read(4); report["slider"] = dragged;
            Check(dragged == "20000" && ReferenceEquals(core, active) && active.CpuCycleUpdates >= 4, "slider changes are acknowledged by the core and change its CPU setting live");
            profile.DosboxSettings["cpu"] = new(StringComparer.OrdinalIgnoreCase) { ["cycles"] = "max 80% limit 50000" }; LoadCpuControls(profile); SaveSelected();
            Check(CpuCycles.Effective(profile) == "max 80% limit 50000", "untouched advanced CPU syntax survives common-panel saves");
            profile.DosboxSettings["cpu"].Remove("cycles"); LoadCpuControls(profile); SetAllPanels(true); await Task.Delay(100);
            CaptureWindow(Path.Combine(output, "cpu-controls.png")); report["passed"] = true;
        }
        catch (Exception e) { report["passed"] = false; report["error"] = e.ToString(); }
        finally
        {
            Stop(); if (profile != null) library.Environments.Remove(profile); library.Save(); environments.SelectedItem = original;
            report["count"] = checks.Count; report["checks"] = checks;
            Paths.AtomicWrite(Path.Combine(output, "cpu-validation.json"), JsonSerializer.Serialize(report, Paths.Json)); Close();
        }
    }
}

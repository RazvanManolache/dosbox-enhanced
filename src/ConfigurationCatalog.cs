using System.Text.RegularExpressions;

namespace DosboxEnhanced;

public sealed record DosSetting(string Group, string Section, string Key, string Name, string Help, string[] Choices, int? Minimum = null, int? Maximum = null);
public static class ConfigurationCatalog
{
    static DosSetting S(string group, string section, string key, string name, string help, string choices = "", int? min = null, int? max = null) => new(group, section, key, name, help, choices.Length == 0 ? [] : choices.Split('|'), min, max);
    public static readonly DosSetting[] Settings =
    [
        S("CPU & memory", "cpu", "core", "CPU emulation core", "Normal favors compatibility; dynamic favors speed; auto chooses for the program.", "auto|normal|simple|dynamic"),
        S("CPU & memory", "cpu", "cputype", "CPU model", "Match older software to a processor model.", "auto|386|386_slow|486_slow|pentium_slow|386_prefetch"),
        S("CPU & memory", "cpu", "cycles", "CPU speed", "Use auto, max, a fixed number, or advanced syntax such as max 80% limit 50000.", "auto|max|3000|5000|10000|20000|50000"),
        S("CPU & memory", "cpu", "cycleup", "Speed increase step", "DOSBox cycle adjustment increment.", "10|20|100|1000", 1, 1000000),
        S("CPU & memory", "cpu", "cycledown", "Speed decrease step", "DOSBox cycle adjustment decrement.", "10|20|100|1000", 1, 1000000),
        S("CPU & memory", "dosbox", "memsize", "RAM (MB)", "Memory available to DOS programs. Older games may need less; this DOSBox Pure core supports up to 1024 MB.", "1|2|4|8|16|32|64|96|128|224|256|512|1024", 1, 1024),
        S("Video", "dosbox", "machine", "Graphics adapter", "Choose the video hardware expected by the game.", "svga_s3|vgaonly|ega|cga|tandy|pcjr|hercules|svga_et3000|svga_et4000|svga_paradise|vesa_nolfb|vesa_oldvbe"),
        S("Video", "render", "aspect", "DOS aspect correction", "Core aspect metadata; display fitting and DLSS effects also have their own controls.", "true|false"),
        S("Video", "render", "frameskip", "Skipped frames", "Leave at zero for a continuous source stream to NVIDIA Frame Generation.", "0|1|2|3|4|5", 0, 10),
        S("Sound Blaster", "sblaster", "sbtype", "Sound card", "Match the game's sound setup. SB16 is a common choice.", "sb16|sbpro2|sbpro1|sb2|sb1|gb|none"),
        S("Sound Blaster", "sblaster", "sbbase", "I/O base address", "Use the same hexadecimal address in the game's setup.", "220|240|260|280"),
        S("Sound Blaster", "sblaster", "irq", "Interrupt (IRQ)", "Must match the game sound setup.", "7|5|3|9|10|11|12"),
        S("Sound Blaster", "sblaster", "dma", "8-bit DMA channel", "Must match the game sound setup.", "1|0|3"),
        S("Sound Blaster", "sblaster", "hdma", "16-bit DMA channel", "High DMA channel for Sound Blaster 16.", "5|6|7|0|1|3"),
        S("Sound Blaster", "sblaster", "sbmixer", "Game-controlled mixer", "Allow game software to change the Sound Blaster mixer.", "true|false"),
        S("Sound Blaster", "sblaster", "oplmode", "FM synthesis", "AdLib / OPL hardware used for music.", "auto|opl2|dualopl2|opl3|none"),
        S("Sound Blaster", "sblaster", "oplemu", "FM synthesis engine", "Available implementations depend on the embedded core.", "default|compat|fast"),
        S("Sound Blaster", "sblaster", "oplrate", "FM sample rate", "Internal FM synthesis sample rate.", "22050|44100|48000|49716", 8000, 96000),
        S("Mixer & MIDI", "mixer", "nosound", "Disable sound emulation", "The toolbar mute keeps emulation running; this disables mixer output.", "false|true"),
        S("Mixer & MIDI", "mixer", "rate", "Mixer sample rate", "The core selects the final output rate for its audio callback.", "22050|44100|48000", 8000, 96000),
        S("Mixer & MIDI", "mixer", "blocksize", "Audio block size", "Smaller buffers reduce latency; larger buffers may avoid crackling.", "256|512|1024|2048|4096|8192", 64, 8192),
        S("Mixer & MIDI", "mixer", "prebuffer", "Prebuffer (ms)", "Extra buffering for steady playback.", "5|10|20|30|50|100", 0, 100),
        S("Mixer & MIDI", "midi", "mpu401", "MIDI interface", "Intelligent mode supports older MPU-401 games.", "intelligent|uart|none"),
        S("Mixer & MIDI", "midi", "mididevice", "MIDI output", "DOSBox Pure also exposes its MIDI/soundfont options in Core options. Host backends vary by core.", "default|none"),
        S("Mixer & MIDI", "midi", "midiconfig", "MIDI output configuration", "Backend-specific device or configuration string."),
        S("GUS & speaker", "gus", "gus", "Gravis UltraSound", "Enable only when the game uses GUS and its DOS drivers are available.", "false|true"),
        S("GUS & speaker", "gus", "gusrate", "GUS sample rate", "Internal GUS synthesis rate.", "22050|44100|48000", 8000, 96000),
        S("GUS & speaker", "gus", "gusbase", "GUS base address", "Match the game's GUS setup.", "240|220|260|280"),
        S("GUS & speaker", "gus", "gusirq", "GUS IRQ", "Match the game's GUS setup.", "5|7|3|9|10|11|12"),
        S("GUS & speaker", "gus", "gusdma", "GUS DMA", "Match the game's GUS setup.", "3|0|1|5|6|7"),
        S("GUS & speaker", "gus", "ultradir", "ULTRASND driver folder", "DOS path to the GUS drivers, for example C:\\ULTRASND."),
        S("GUS & speaker", "speaker", "pcspeaker", "PC speaker", "Enable the classic PC speaker.", "true|false"),
        S("GUS & speaker", "speaker", "pcrate", "PC speaker sample rate", "Internal speaker synthesis rate.", "22050|44100|48000", 8000, 96000),
        S("GUS & speaker", "speaker", "tandy", "Tandy audio", "Auto enables Tandy sound with Tandy / PCjr video hardware.", "auto|on|off"),
        S("GUS & speaker", "speaker", "tandyrate", "Tandy sample rate", "Internal Tandy sound rate.", "22050|44100|48000", 8000, 96000),
        S("GUS & speaker", "speaker", "disney", "Disney Sound Source", "Parallel-port sound hardware emulation.", "true|false"),
        S("DOS services", "dos", "xms", "Extended memory (XMS)", "Some older software requires this disabled.", "true|false"),
        S("DOS services", "dos", "ems", "Expanded memory (EMS)", "Enable for applications that use EMS.", "true|false"),
        S("DOS services", "dos", "umb", "Upper memory blocks", "Allow DOS programs to use upper memory.", "true|false"),
        S("DOS services", "dos", "keyboardlayout", "Keyboard layout", "DOS keyboard code; auto, none, us, uk, de, fr and other installed layouts.", "auto|none|us|uk|de|fr|ro"),
        S("Input", "joystick", "joysticktype", "Joystick hardware", "Choose the emulated DOS joystick type. Physical controller support depends on host input support.", "auto|none|2axis|4axis|4axis_2|fcs|ch"),
        S("Input", "joystick", "timed", "Timed joystick reads", "Can improve compatibility with games that use joystick timing.", "true|false"),
        S("Input", "joystick", "autofire", "Joystick autofire", "Automatic repeated joystick button presses.", "false|true"),
        S("Input", "joystick", "swap34", "Swap axes 3 / 4", "Adjust the emulated four-axis joystick.", "false|true"),
        S("Input", "joystick", "buttonwrap", "Wrap joystick buttons", "Map extra joystick buttons onto the DOS button count.", "false|true"),
        S("Ports & network", "serial", "serial1", "COM1", "Device plus optional parameters, such as nullmodem server:127.0.0.1.", "dummy|disabled|modem|nullmodem"),
        S("Ports & network", "serial", "serial2", "COM2", "Serial emulation; support depends on the core.", "dummy|disabled|modem|nullmodem"),
        S("Ports & network", "serial", "serial3", "COM3", "Serial emulation; support depends on the core.", "disabled|dummy|modem|nullmodem"),
        S("Ports & network", "serial", "serial4", "COM4", "Serial emulation; support depends on the core.", "disabled|dummy|modem|nullmodem"),
        S("Ports & network", "ipx", "ipx", "IPX networking", "DOS game networking, separate from the AI / MCP connection permission.", "false|true")
    ];
    public static void Validate(DosSetting definition, string value)
    {
        if (value.Length == 0) return;
        if (definition.Minimum is int min && (!int.TryParse(value, out int n) || n < min || n > definition.Maximum)) throw new ArgumentException($"{definition.Name}: enter a number from {min} to {definition.Maximum}.");
        if (definition.Section == "cpu" && definition.Key == "cycles" && !Regex.IsMatch(value, "^(auto|max|[1-9][0-9]*|fixed [1-9][0-9]*)( [0-9]+%?| limit [1-9][0-9]*)*$")) throw new ArgumentException("CPU speed: use auto, max, a positive number or valid DOSBox cycle syntax.");
    }
}

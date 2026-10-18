using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XboxRumble;

internal static class RumbleUiTests
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static T Find<T>(MainWindow view, string name) where T : class
    { return view.Window.FindName(name) as T; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application app = new Application();
            FakeController fake = new FakeController();
            using (RumbleHost host = new RumbleHost(fake))
            using (MainWindow view = new MainWindow(fake, host, false))
            {
                ListBox modes = Find<ListBox>(view, "Modes");
                Button start = Find<Button>(view, "Start");
                Button stop = Find<Button>(view, "Stop");
                TextBox duration = Find<TextBox>(view, "Duration");
                ComboBox devices = Find<ComboBox>(view, "Devices");
                Assert(modes.Items.Count == 17, "All rhythm modes plus motor melody are visible");
                Assert(start.IsEnabled && !host.Read().Active && fake.Outputs.Count == 0, "A connected controller stays idle until start");
                Assert(((DeviceEntry)devices.SelectedItem).Info.Name == fake.Devices[0].Name && Find<TextBlock>(view, "ConnectionText").Text == "已连接", "Device name binds to connection state");
                foreach (ModeEntry mode in modes.Items)
                {
                    modes.SelectedItem = mode;
                    Assert(Find<TextBlock>(view, "ModeName").Text == mode.Name, "Mode selection updates its heading");
                    Assert(Find<Slider>(view, "Tempo").IsEnabled == (mode.Info.Pattern != Pattern.Constant), "Constant mode disables period control");
                    Assert(mode.Curve != null && !mode.Curve.IsEmpty(), "Every mode has a thumbnail waveform");
                }
                Assert(Find<StackPanel>(view, "MelodyPanel").Visibility == Visibility.Visible, "Melody exposes score selector");
                Assert(Find<ComboBox>(view, "Melodies").Items.Count == 3 && Find<TextBlock>(view, "PeriodValue").Text == "90 BPM", "Three songs and a musical tempo");
                Find<Slider>(view, "Tempo").Value = 120;
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Thread.Sleep(60);
                Assert(host.Read().Active, "Melody starts through the normal guarded output path");
                Find<ComboBox>(view, "Melodies").SelectedIndex = 1;
                Assert(!host.Read().Active && fake.LastIsZero(), "Changing songs stops before restarting from beginning");
                modes.SelectedIndex = 1;
                Assert(Find<StackPanel>(view, "MelodyPanel").Visibility == Visibility.Collapsed && Find<Slider>(view, "Tempo").Maximum == 50, "Regular modes recover their period control");
                modes.SelectedIndex = 16;
                Assert(Find<TextBlock>(view, "PeriodValue").Text == "120 BPM", "Returning to melody preserves its tempo");
                Slider bpm = Find<Slider>(view, "Tempo");
                Assert(bpm.Minimum == 0 && bpm.Maximum == 300, "BPM slider spans 0-300");
                bpm.Value = 0;
                Assert(Find<TextBlock>(view, "PeriodValue").Text == "0 BPM", "Zero BPM is shown explicitly");
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Thread.Sleep(60);
                Assert(host.Read().Active && host.Read().Paused && fake.LastIsZero(), "UI zero BPM starts paused without driving motors");
                bpm.Value = 300; Thread.Sleep(60);
                Assert(!host.Read().Paused && host.Read().Active && host.Read().Right > 0, "UI tempo can resume at 300 BPM");
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bpm.Value = 0; modes.SelectedIndex = 1; modes.SelectedIndex = 16;
                Assert(bpm.Value == 0, "Mode switching preserves paused tempo");
                bpm.Value = 120;
                view.RenderPreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-melody.png"), 944, 780);
                Find<Slider>(view, "LeftMotor").Value = 13;
                Find<Slider>(view, "RightMotor").Value = 37;
                Assert(Find<TextBlock>(view, "LeftValue").Text == "13%" && Find<TextBlock>(view, "RightValue").Text == "37%", "Sliders update percentage labels");
                duration.Text = "86401";
                Assert(!start.IsEnabled, "Out-of-range duration blocks start");
                duration.Text = "";
                Assert(!start.IsEnabled, "Empty duration blocks start");
                RadioButton unlimited = Find<RadioButton>(view, "InfiniteDuration");
                RadioButton timed = Find<RadioButton>(view, "TimedDuration");
                unlimited.IsChecked = true;
                Assert(start.IsEnabled && !duration.IsEnabled && Find<TextBlock>(view, "Remaining").Text == "∞", "Unlimited mode bypasses hidden duration validation and displays infinity");
                timed.IsChecked = true;
                Assert(!start.IsEnabled && duration.IsEnabled, "Returning to timed mode restores validation");
                duration.Text = "3601";
                Assert(start.IsEnabled && Find<TextBlock>(view, "Remaining").Text == "01:00:01", "Long timers display hours");
                duration.Text = "25";
                Assert(start.IsEnabled && Find<TextBlock>(view, "Remaining").Text == "00:25", "Valid duration updates standby time");
                modes.SelectedIndex = 0;
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Thread.Sleep(60);
                Assert(host.Read().Active && !duration.IsEnabled && !devices.IsEnabled, "Start locks duration and target");
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(!host.Read().Active && fake.LastIsZero() && duration.IsEnabled, "Stop button clears output and unlocks editing");
                unlimited.IsChecked = true;
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(!timed.IsEnabled && !unlimited.IsEnabled && !host.Read().Remaining.HasValue, "Running duration mode is locked and passed to engine");
                view.Window.WindowState = WindowState.Minimized;
                // No dispatcher pumping or UI reads during this interval. Only the output thread runs.
                Thread.Sleep(1800);
                Snapshot background = host.Read();
                Assert(background.Active && background.Elapsed >= 1.5 && background.Left > 0, "Minimized playback survives absent UI refreshes");
                view.Window.WindowState = WindowState.Normal;
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(!host.Read().Active && fake.LastIsZero() && unlimited.IsEnabled, "Manual stop works for unlimited playback");
                view.RenderPreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-infinite.png"), 944, 700);
                timed.IsChecked = true;
                Assert(duration.Text == "25" && duration.IsEnabled, "Switching duration mode preserves the last finite value");
                modes.SelectedIndex = 1;
                string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-smoke.png");
                view.RenderPreview(output, 784, 580);
                Assert(new FileInfo(output).Length > 1000, "Compact layout renders without showing a window");
                Melody importedMidi = new Melody("Imported lead", new[] { 60,67 }, new double[] { 1,1 }, 138, "MIDI · Lead", 0.9);
                view.AddImportedSongs(new[] { importedMidi });
                Assert(Find<ComboBox>(view, "Melodies").Items.Count == 4 && !host.Read().Active, "Imported MIDI is selectable without auto-playing");
                Assert(Find<TextBlock>(view, "PeriodValue").Text == "138 BPM" && Find<TextBlock>(view, "SongDetails").Text.Contains("Lead"), "MIDI import selects source tempo and voice description");
                Melody importedAudio = new Melody("Imported MP3", new[] { 60,64 }, new double[] { 1,1 }, 120, "音频 · 实验性音高识别", 1, true);
                view.AddImportedSongs(new[] { importedAudio });
                Assert(Find<TextBlock>(view, "PeriodValue").Text == "100%", "Audio speed is displayed as original-speed percentage");
                view.AddImportedSongs(new[] { importedAudio });
                Assert(Find<ComboBox>(view, "Melodies").Items.Count == 5, "Reimport replaces same-name song instead of duplicating it");
                view.RenderPreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-import.png"), 944, 820);
            }
            string fixtures = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "song-fixtures");
            using (RumbleHost host = new RumbleHost(new FakeController()))
            using (MainWindow view = new MainWindow(new FakeController(), host, true, Path.Combine(fixtures, "ui-cache")))
            {
                Find<ListBox>(view, "Modes").SelectedIndex = 16;
                string noNotes = args.Length == 1 ? args[0] : Path.Combine(fixtures, "Tempo only.mid");
                Task<bool> failed = view.ImportSongAsync(noNotes);
                Assert(!Find<Button>(view, "ImportSong").IsEnabled && Find<TextBlock>(view, "ImportStatus").Text.Contains(Path.GetFileName(noNotes)), "Async import immediately acknowledges the selected file");
                Assert(!view.ImportSongAsync(noNotes).Result, "Duplicate imports are blocked during processing");
                Assert(!Pump(failed), "An empty export fails without losing dispatcher completion");
                TextBlock result = Find<TextBlock>(view, "ImportStatus");
                Assert(result.Visibility == Visibility.Visible && result.Text.Contains("没有音符") && result.Text.Contains("重新导出"), "Import failure is visible beside the picker, with a specific remedy");
                Assert(Find<Button>(view, "ImportSong").IsEnabled && Find<ComboBox>(view, "Melodies").Items.Count == 3, "Failure restores picker and preserves existing songs");
                Console.WriteLine("Checked import: " + result.Text);
                view.RenderPreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-import-error.png"), 944, 820);
                Assert(Pump(view.ImportSongAsync(Path.Combine(fixtures, "300 BPM.mid"))), "Successful import works immediately after failure");
                Melody imported = (Melody)Find<ComboBox>(view, "Melodies").SelectedItem;
                Assert(imported.DefaultBpm == 300 && Find<TextBlock>(view, "PeriodValue").Text == "300 BPM", "Full async path selects imported voice and original high tempo");
                Assert(result.Text.Contains("1 个声部") && !host.Read().Active, "Successful import reports voice count without auto-playing");
                Find<Slider>(view, "Tempo").Value = 0;
                Assert(Find<TextBlock>(view, "SongDetails").Text.Contains("已暂停"), "Imported song duration handles zero BPM without division by zero");
                view.RenderPreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-zero-bpm.png"), 944, 820);
            }
            FakeController mixed = new FakeController();
            mixed.Devices.Clear();
            mixed.Devices.Add(41, new ControllerDevice(41, "DualSense", "PlayStation", "有线", RumbleCapability.DualChannel, "○"));
            mixed.Devices.Add(75, new ControllerDevice(75, "Switch Pro Controller", "Nintendo Switch", "无线", RumbleCapability.DualChannel, "B"));
            mixed.Devices.Add(99, new ControllerDevice(99, "Generic USB Gamepad", "通用手柄", "有线", RumbleCapability.None, ""));
            using (RumbleHost host = new RumbleHost(mixed))
            using (MainWindow view = new MainWindow(mixed, host, false))
            {
                ComboBox devices = Find<ComboBox>(view, "Devices");
                Assert(devices.Items.Count == 3 && ((DeviceEntry)devices.SelectedItem).Id == 41, "Mixed device list is dynamic");
                Assert(Find<TextBlock>(view, "Shortcuts").Text.StartsWith("○ / Esc"), "PlayStation stop prompt uses circle");
                devices.SelectedIndex = 1;
                Assert(Find<TextBlock>(view, "Shortcuts").Text.StartsWith("B / Esc"), "Nintendo stop prompt uses B");
                Find<ListBox>(view, "Modes").SelectedIndex = 0;
                Find<Button>(view, "Start").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Thread.Sleep(60);
                Find<Button>(view, "Stop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(mixed.Outputs[mixed.Outputs.Count - 1].Index == 75, "UI routes output by selected stable ID");
                devices.SelectedIndex = 2;
                Assert(!Find<Button>(view, "Start").IsEnabled && Find<TextBlock>(view, "ConnectionText").Text == "不支持震动", "Unsupported rumble is visibly disabled");
                Assert(Find<TextBlock>(view, "Shortcuts").Text.StartsWith("Esc"), "Unmapped controller never advertises a guessed stop button");
            }
            Console.WriteLine("PASS: " + checks + " UI assertions; mode switching, labels, duration validation, start/stop, offscreen layout. Fake controller only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static bool Pump(Task<bool> task)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed.TotalSeconds < 10)
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        Assert(task.IsCompleted, "Import finishes within test deadline");
        return task.GetAwaiter().GetResult();
    }
}

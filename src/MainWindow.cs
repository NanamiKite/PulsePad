using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XboxRumble
{
    internal sealed class DeviceEntry
    {
        public readonly ControllerDevice Info;
        public int Id { get { return Info == null ? -1 : Info.Id; } }
        public DeviceEntry(ControllerDevice info) { Info = info; }
        public string Name { get { return Info == null ? "未检测到手柄" : Info.Name; } }
    }

    internal sealed class ModeEntry
    {
        public PatternInfo Info { get; private set; }
        public string Name { get { return Info.Name; } }
        public Geometry Curve { get; private set; }
        public ModeEntry(PatternInfo info)
        {
            Info = info;
            Settings settings = new Settings(1, 1, 1, info.Pattern);
            StreamGeometry curve = new StreamGeometry();
            using (StreamGeometryContext context = curve.Open())
            {
                for (int i = 0; i <= 64; i++)
                {
                    ushort left, right;
                    settings.Sample(i * 2.0 / 64, out left, out right);
                    Point point = new Point(i * 42.0 / 64, 12 - left / 65535.0 * 11);
                    if (i == 0) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                }
            }
            curve.Freeze(); Curve = curve;
        }
    }

    internal sealed class WavePlot : FrameworkElement
    {
        public Settings Settings;
        public bool Playing;
        public double Elapsed;
        private static readonly Brush Green = Brush("#B3CC91");
        private static readonly Brush Blue = Brush("#8FA6BD");
        private static readonly Pen GridPen = new Pen(Brush("#30363D"), 1);
        private static Brush Brush(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze(); return brush;
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (Settings == null || ActualWidth < 2 || ActualHeight < 2) return;
            double width = ActualWidth, height = ActualHeight;
            for (int i = 0; i <= 6; i++)
                drawing.DrawLine(GridPen, new Point(i * width / 6, 0), new Point(i * width / 6, height));
            for (int channel = 0; channel < 2; channel++)
            {
                double baseline = (channel + 1) * height / 2 - 4;
                double amplitude = height / 2 - 10;
                drawing.DrawLine(GridPen, new Point(0, baseline), new Point(width, baseline));
                StreamGeometry curve = new StreamGeometry();
                using (StreamGeometryContext context = curve.Open())
                {
                    int samples = Math.Max(120, (int)width);
                    for (int i = 0; i <= samples; i++)
                    {
                        ushort l, r;
                        double offset = Playing ? Math.Floor(Elapsed / 6) * 6 : 0;
                        Settings.Sample(offset + 6.0 * i / samples, out l, out r);
                        double value = (channel == 0 ? l : r) / 65535.0;
                        Point point = new Point(width * i / samples, baseline - amplitude * value);
                        if (i == 0) context.BeginFigure(point, false, false);
                        else context.LineTo(point, true, false);
                    }
                }
                drawing.DrawGeometry(null, new Pen(channel == 0 ? Green : Blue, 1.6), curve);
            }
            if (Playing && !Settings.Paused)
            {
                double x = (Elapsed % 6) / 6 * width;
                drawing.DrawLine(new Pen(Brush("#6D777F"), 1), new Point(x, 0), new Point(x, height));
            }
        }
    }

    internal sealed class MainWindow : IDisposable
    {
        public readonly Window Window;
        private readonly IController controller;
        private readonly RumbleHost host;
        private readonly ComboBox devices;
        private readonly ComboBox melodies;
        private readonly ObservableCollection<Melody> songChoices = new ObservableCollection<Melody>(MelodyLibrary.All);
        private readonly CancellationTokenSource importCancellation = new CancellationTokenSource();
        private readonly string songDirectory;
        private bool importing, importFailed;
        private string importNotice = "";
        private readonly ListBox modes;
        private readonly Slider left, right, tempo;
        private readonly TextBox duration;
        private readonly RadioButton timedDuration, infiniteDuration;
        private readonly TextBlock leftValue, rightValue, periodValue, status, remaining, modeName, modeDescription, connectionText, shortcuts;
        private readonly System.Windows.Shapes.Ellipse connectionDot;
        private readonly Button start, stop;
        private readonly WavePlot plot = new WavePlot();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly bool preview;
        private DateTime nextScan = DateTime.MinValue;
        private bool scanning, registered, disposed;
        private bool hotkeyUnavailable;
        private bool configuring, melodyTempo;
        private double regularTempo = 15, musicTempo = 90;
        private HwndSource source;
        private const int HotkeyId = 0x5842;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public MainWindow(IController controller, RumbleHost host, bool preview, string songDirectory = null)
        {
            this.controller = controller; this.host = host; this.preview = preview;
            this.songDirectory = songDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "songs");
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("XboxRumble.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(xaml);
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("XboxRumble.Icon"))
            {
                BitmapFrame frame = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                Window.Icon = frame;
            }
            devices = Find<ComboBox>("Devices"); modes = Find<ListBox>("Modes");
            melodies = Find<ComboBox>("Melodies"); melodies.ItemsSource = songChoices; melodies.SelectedIndex = 0;
            left = Find<Slider>("LeftMotor"); right = Find<Slider>("RightMotor"); tempo = Find<Slider>("Tempo");
            duration = Find<TextBox>("Duration");
            timedDuration = Find<RadioButton>("TimedDuration"); infiniteDuration = Find<RadioButton>("InfiniteDuration");
            leftValue = Find<TextBlock>("LeftValue"); rightValue = Find<TextBlock>("RightValue"); periodValue = Find<TextBlock>("PeriodValue");
            status = Find<TextBlock>("Status"); remaining = Find<TextBlock>("Remaining");
            modeName = Find<TextBlock>("ModeName"); modeDescription = Find<TextBlock>("ModeDescription");
            connectionText = Find<TextBlock>("ConnectionText"); connectionDot = Find<System.Windows.Shapes.Ellipse>("ConnectionDot");
            shortcuts = Find<TextBlock>("Shortcuts"); start = Find<Button>("Start"); stop = Find<Button>("Stop");
            Find<Grid>("GraphHost").Children.Add(plot);
            devices.Items.Add(new DeviceEntry(null)); devices.SelectedIndex = 0;
            List<ModeEntry> entries = new List<ModeEntry>();
            foreach (PatternInfo info in PatternLibrary.All) entries.Add(new ModeEntry(info));
            modes.ItemsSource = entries; modes.SelectedIndex = 1;
            Find<TextBlock>("ModeCount").Text = entries.Count + " 种模式";

            modes.SelectionChanged += delegate { Configure(); UpdateStatus(); };
            melodies.SelectionChanged += delegate
            {
                host.Stop("已切换曲目，点击开始从头播放");
                Melody song = melodies.SelectedItem as Melody;
                if (song != null && !string.IsNullOrEmpty(song.Detail))
                {
                    musicTempo = song.DefaultBpm; if (melodyTempo) tempo.Value = musicTempo;
                    if (timedDuration.IsChecked == true) duration.Text = Math.Max(1, Math.Min(RumbleSession.MaximumDurationSeconds, Math.Ceiling(song.Beats * 60 / musicTempo))).ToString("0", CultureInfo.InvariantCulture);
                }
                Configure(); UpdateStatus();
            };
            Find<Button>("ImportSong").Click += delegate { ChooseSong(); };
            left.ValueChanged += delegate { Configure(); };
            right.ValueChanged += delegate { Configure(); };
            tempo.ValueChanged += delegate { Configure(); };
            devices.SelectionChanged += delegate
            {
                if (!scanning) { host.Stop("已切换手柄"); UpdateStatus(); }
            };
            duration.TextChanged += delegate { UpdateStatus(); };
            timedDuration.Checked += delegate { UpdateStatus(); };
            infiniteDuration.Checked += delegate { UpdateStatus(); };
            start.Click += delegate
            {
                importNotice = "";
                double? seconds;
                if (!TryDuration(out seconds)) { status.Text = "播放时长请输入 1–86400 秒"; duration.Focus(); return; }
                DeviceEntry selected = devices.SelectedItem as DeviceEntry;
                Configure(); host.Start(selected == null ? -1 : selected.Id, seconds); UpdateStatus();
            };
            stop.Click += delegate { importNotice = ""; host.Stop("已停止"); UpdateStatus(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { host.Stop("已停止"); UpdateStatus(); e.Handled = true; }
            };
            Window.SourceInitialized += OnSourceInitialized;
            Window.Closing += delegate { timer.Stop(); host.Stop("已关闭"); };
            Window.Closed += delegate { Dispose(); };
            timer.Interval = TimeSpan.FromMilliseconds(80);
            timer.Tick += delegate { UpdateStatus(); };
            Configure();
            if (!preview)
            {
                string error;
                foreach (Melody song in SongFiles.LoadFolder(this.songDirectory, out error)) songChoices.Add(song);
                if (!string.IsNullOrEmpty(error)) { importFailed = true; importNotice = "部分曲目未加载：" + error; }
            }
            if (!preview) Scan();
            UpdateStatus();
            if (!preview)
            {
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 24));
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 24));
                timer.Start();
            }
        }

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private async void ChooseSong()
        {
            host.Stop("选择曲目，播放已停止"); UpdateStatus();
            try
            {
                Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入曲目", Filter = "音乐文件|*.mid;*.midi;*.mp3;*.wav|MIDI 乐谱|*.mid;*.midi|MP3 / WAV 音频|*.mp3;*.wav",
                    CheckFileExists = true, Multiselect = false
                };
                if (dialog.ShowDialog(Window) == true) await ImportSongAsync(dialog.FileName);
            }
            catch (Exception problem) { importFailed = true; importNotice = "无法导入：" + problem.Message; UpdateStatus(); }
        }
        // The file picker and regression tests use the same complete asynchronous path.
        internal async Task<bool> ImportSongAsync(string path)
        {
            Dispatcher dispatcher = Window.Dispatcher;
            dispatcher.VerifyAccess();
            if (disposed || importing) return false;
            host.Stop("导入曲目，播放已停止");
            importing = true; importFailed = false;
            importNotice = "正在读取「" + Path.GetFileName(path) + "」…"; UpdateStatus();
            Melody[] imported = null; string error = "", saveError = "";
            await Task.Run(delegate
            {
                try
                {
                    imported = SongFiles.Import(path, importCancellation.Token);
                    importCancellation.Token.ThrowIfCancellationRequested();
                    try { SongFiles.SaveImported(songDirectory, imported); }
                    catch (Exception problem) { saveError = "本次可播放，但未能保存曲目：" + problem.Message; }
                }
                catch (OperationCanceledException) { imported = null; error = "已取消导入"; }
                catch (Exception problem) { imported = null; error = problem.Message; }
            }).ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted) return false;
            try
            {
                return await dispatcher.InvokeAsync<bool>(delegate
                {
                    if (disposed) return false;
                    importing = false;
                    try
                    {
                        if (imported == null) throw new InvalidDataException(error);
                        AddImportedSongs(imported);
                        importNotice = "已导入「" + Path.GetFileName(path) + "」· " + imported.Length + " 个声部，点击开始播放。";
                        if (!string.IsNullOrEmpty(saveError)) importNotice += "\n" + saveError;
                        return true;
                    }
                    catch (Exception problem)
                    {
                        importFailed = true;
                        importNotice = "「" + Path.GetFileName(path) + "」导入失败：" + problem.Message;
                        return false;
                    }
                    finally { UpdateStatus(); }
                }).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; } // Dispatcher stopped on application exit.
        }
        internal void AddImportedSongs(Melody[] imported)
        {
            host.Stop("已导入曲目，点击开始播放");
            foreach (Melody song in imported)
            {
                for (int i = songChoices.Count - 1; i >= MelodyLibrary.All.Length; i--) if (songChoices[i].Name == song.Name) songChoices.RemoveAt(i);
                songChoices.Add(song);
            }
            foreach (ModeEntry mode in modes.Items) if (mode.Info.Pattern == Pattern.Melody) { modes.SelectedItem = mode; break; }
            if (imported.Length > 0) melodies.SelectedItem = imported[0];
            Configure(); UpdateStatus();
        }
        private void Configure()
        {
            if (configuring) return;
            ModeEntry selected = modes.SelectedItem as ModeEntry;
            if (selected == null) return;
            bool music = selected.Info.Pattern == Pattern.Melody;
            Melody currentSong = melodies.SelectedItem as Melody;
            configuring = true;
            try
            {
                if (music != melodyTempo)
                {
                    if (melodyTempo) musicTempo = tempo.Value; else regularTempo = tempo.Value;
                    // Reset bounds before setting the value, without intermediate Configure calls.
                    tempo.Minimum = 0; tempo.Maximum = music ? 300 : 50;
                    tempo.Minimum = music ? 0 : PatternLibrary.MinimumPeriod(selected.Info.Pattern) * 10;
                    tempo.Value = music ? musicTempo : regularTempo;
                    host.Stop("已切换播放类型，点击开始从头播放");
                    melodyTempo = music;
                }
                else tempo.Minimum = music ? 0 : PatternLibrary.MinimumPeriod(selected.Info.Pattern) * 10;
            }
            finally { configuring = false; }
            bool audioSong = music && currentSong != null && currentSong.IsAudio;
            tempo.ToolTip = audioSong ? "0–250%；100% 为音频原速，0% 暂停，调高继续" : music ? "0–300 BPM；0 暂停音符和马达，调高速度继续；定时仍会倒计时" : "每轮 " + (tempo.Minimum / 10).ToString("0.0", CultureInfo.InvariantCulture) + "–5.0 秒";
            Find<TextBlock>("TempoLabel").Text = music ? "速度" : "周期";
            Find<StackPanel>("MelodyPanel").Visibility = music ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("LeftRole").Text = music ? "打拍子" : "低频";
            Find<TextBlock>("RightRole").Text = music ? "旋律" : "高频";
            TextBlock details = Find<TextBlock>("SongDetails");
            details.Text = currentSong == null ? "" : currentSong.IsAudio ? "音频" : currentSong.Detail;
            if (currentSong != null && !string.IsNullOrEmpty(details.Text))
            {
                if (music && tempo.Value == 0) details.Text += " · 已暂停，调高速度继续";
                else
                {
                    int songSeconds = (int)Math.Ceiling(currentSong.Beats * 60 / (music ? tempo.Value : currentSong.DefaultBpm));
                    details.Text += " · " + (songSeconds / 60).ToString("00") + ":" + (songSeconds % 60).ToString("00");
                }
            }
            details.Visibility = music && !string.IsNullOrEmpty(details.Text) ? Visibility.Visible : Visibility.Collapsed;
            Settings settings = new Settings(left.Value / 100, right.Value / 100, music ? (tempo.Value == 0 ? double.PositiveInfinity : 60 / tempo.Value) : tempo.Value / 10, selected.Info.Pattern, melodies.SelectedItem as Melody);
            host.Configure(settings); plot.Settings = settings; plot.InvalidateVisual();
            leftValue.Text = left.Value.ToString("0") + "%";
            rightValue.Text = right.Value.ToString("0") + "%";
            periodValue.Text = selected.Info.Pattern == Pattern.Constant ? "—" : audioSong ? (tempo.Value / 120 * 100).ToString("0") + "%" : music ? tempo.Value.ToString("0") + " BPM" : (tempo.Value / 10).ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
            tempo.IsEnabled = selected.Info.Pattern != Pattern.Constant;
            modeName.Text = selected.Name; modeDescription.Text = selected.Info.Description;
            modeDescription.Visibility = string.IsNullOrEmpty(modeDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        private bool TryDuration(out double? seconds)
        {
            seconds = null;
            if (infiniteDuration.IsChecked == true) return true;
            int value;
            if (!int.TryParse(duration.Text, out value) || value < 1 || value > RumbleSession.MaximumDurationSeconds) return false;
            seconds = value; return true;
        }
        private void Scan()
        {
            scanning = true;
            try
            {
                DeviceEntry previous = devices.SelectedItem as DeviceEntry;
                int previousId = previous == null ? -1 : previous.Id;
                ControllerDevice[] connected = controller.Enumerate();
                devices.Items.Clear();
                DeviceEntry selection = null;
                foreach (ControllerDevice info in connected)
                {
                    DeviceEntry entry = new DeviceEntry(info);
                    devices.Items.Add(entry);
                    if (info.Id == previousId) selection = entry;
                }
                if (devices.Items.Count == 0) devices.Items.Add(new DeviceEntry(null));
                if (selection != null) devices.SelectedItem = selection;
                else devices.SelectedIndex = 0;
                nextScan = DateTime.UtcNow.AddSeconds(1);
            }
            finally { scanning = false; }
        }
        private void UpdateStatus()
        {
            Snapshot state = host.Read();
            if (!preview && !state.Active && DateTime.UtcNow >= nextScan) Scan();
            DeviceEntry selected = devices.SelectedItem as DeviceEntry;
            ControllerDevice device = selected == null ? null : selected.Info;
            bool present = device != null && controller.GetDevice(device.Id) != null;
            bool capable = present && device.CanRumble;
            connectionText.Text = !present ? "未连接" : capable ? "已连接" : "不支持震动";
            connectionDot.Fill = (Brush)new BrushConverter().ConvertFromString(present ? "#B3CC91" : "#737B84");
            double? configuredSeconds;
            bool validDuration = TryDuration(out configuredSeconds);
            start.IsEnabled = capable && !state.Active && validDuration && !importing;
            Find<Button>("ImportSong").IsEnabled = !importing;
            Find<Button>("ImportSong").Content = importing ? "分析中…" : "导入…";
            TextBlock importStatus = Find<TextBlock>("ImportStatus");
            importStatus.Text = importNotice;
            importStatus.Visibility = string.IsNullOrEmpty(importNotice) ? Visibility.Collapsed : Visibility.Visible;
            importStatus.Foreground = (Brush)new BrushConverter().ConvertFromString(importFailed ? "#E0A6AB" : "#B3CC91");
            start.Content = state.Active ? (state.Paused ? "已暂停" : "播放中") : "开始";
            devices.IsEnabled = timedDuration.IsEnabled = infiniteDuration.IsEnabled = !state.Active;
            bool unlimited = infiniteDuration.IsChecked == true;
            duration.IsEnabled = !state.Active && !unlimited;
            duration.Visibility = Find<TextBlock>("DurationUnit").Visibility = unlimited ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("InfiniteHint").Visibility = unlimited ? Visibility.Visible : Visibility.Collapsed;
            status.Text = !present && state.Message == "待机" ? "连接并唤醒手柄后即可开始" : state.Message;
            if (present && !capable) status.Text = "此设备或当前驱动未提供震动支持";
            if (!string.IsNullOrEmpty(controller.Notice) && state.Message == "待机") status.Text = controller.Notice;
            if (!validDuration) status.Text = "播放时长请输入 1–86400 秒";
            if (!state.Active && validDuration && !string.IsNullOrEmpty(importNotice)) status.Text = importNotice;
            status.ToolTip = status.Text;
            TextBlock detail = Find<TextBlock>("DeviceDetails");
            detail.Text = present ? device.Description : "通过 USB、蓝牙或无线接收器连接手柄";
            detail.ToolTip = present ? device.Name + "\n" + device.Description : detail.Text;
            devices.ToolTip = detail.ToolTip;
            string stopKey = present && !string.IsNullOrEmpty(device.StopButtonLabel) ? device.StopButtonLabel + " / Esc" : "Esc";
            shortcuts.Text = stopKey + " 停止     ·     " + (hotkeyUnavailable ? "全局快捷键被占用" : "Ctrl+Shift+Space 全局停止");
            double? remainingSeconds = state.Active ? state.Remaining : validDuration ? configuredSeconds : 0;
            remaining.FontSize = remainingSeconds.HasValue && remainingSeconds.Value >= 3600 ? 20 : 26;
            if (!remainingSeconds.HasValue) remaining.Text = "∞";
            else
            {
                int seconds = (int)Math.Ceiling(remainingSeconds.Value);
                remaining.Text = (seconds >= 3600 ? (seconds / 3600).ToString("00") + ":" : "") + (seconds / 60 % 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            }
            remaining.ToolTip = !remainingSeconds.HasValue ? "无限播放 · 手动停止" : "剩余播放时间";
            bool redraw = plot.Playing || state.Active;
            plot.Playing = state.Active; plot.Elapsed = state.Elapsed;
            if (plot.Settings.Pattern == Pattern.Melody) plot.Elapsed = plot.Settings.Paused ? 0 : state.MelodyBeat * plot.Settings.Period;
            if (redraw && Window.WindowState != WindowState.Minimized) plot.InvalidateVisual();
        }
        private void OnSourceInitialized(object sender, EventArgs args)
        {
            if (preview) return;
            IntPtr handle = new WindowInteropHelper(Window).Handle;
            int dark = 1;
            try { DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) { }
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WindowMessage);
            registered = RegisterHotKey(handle, HotkeyId, 0x0002 | 0x0004 | 0x4000, 0x20);
            hotkeyUnavailable = !registered;
            UpdateStatus();
        }
        private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
            { host.Stop("已停止"); UpdateStatus(); handled = true; }
            if (message == 0x0218 && wParam.ToInt32() == 4) host.Stop("电脑已进入睡眠，播放已停止");
            return IntPtr.Zero;
        }
        public void RenderPreview(string path, int width = 944, int height = 700)
        {
            // Offscreen layout; this never opens or activates a desktop window.
            FrameworkElement root = (FrameworkElement)Window.Content;
            root.Width = width; root.Height = height;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            root.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            RenderTargetBitmap image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(root);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream file = File.Create(path)) encoder.Save(file);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); importCancellation.Cancel();
            host.Stop("已关闭");
            if (source != null)
            {
                if (registered) UnregisterHotKey(source.Handle, HotkeyId);
                source.RemoveHook(WindowMessage);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace XboxRumble
{
    public enum Pattern
    {
        Constant, Pulse, Breathe, DoublePulse, Alternate,
        TriplePulse, Heartbeat, RampUp, RampDown, Triangle,
        Crossfade, Burst, StepUp, StepDown, Random, Wave, Melody
    }

    public sealed class PatternInfo
    {
        public Pattern Pattern { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public PatternInfo(Pattern pattern, string name, string description)
        { Pattern = pattern; Name = name; Description = description; }
    }

    public static class PatternLibrary
    {
        public static double MinimumPeriod(Pattern pattern)
        {
            // Leave at least two output ticks for each short pulse.
            if (pattern == Pattern.Burst) return 0.8;
            if (pattern == Pattern.TriplePulse || pattern == Pattern.Heartbeat) return 0.5;
            if (pattern == Pattern.DoublePulse) return 0.4;
            if (pattern == Pattern.Melody) return 60.0 / 300;
            return 0.2;
        }
        public static readonly PatternInfo[] All = {
            new PatternInfo(Pattern.Constant, "持续", "保持设定强度"),
            new PatternInfo(Pattern.Breathe, "呼吸", "平滑地增强，再慢慢减弱"),
            new PatternInfo(Pattern.Wave, "起伏", "在三成与满强度之间柔和变化"),
            new PatternInfo(Pattern.Pulse, "脉冲", "震动半拍，停半拍"),
            new PatternInfo(Pattern.DoublePulse, "双击", "连续两下，然后停顿"),
            new PatternInfo(Pattern.TriplePulse, "三连击", "连续三下，然后停顿"),
            new PatternInfo(Pattern.Heartbeat, "心跳", "一下重、一下轻，留一段间隔"),
            new PatternInfo(Pattern.Burst, "短连发", "快速震动四下，再休息半拍"),
            new PatternInfo(Pattern.RampUp, "渐强", "从零逐渐增强，随后重新开始"),
            new PatternInfo(Pattern.RampDown, "渐弱", "从设定强度逐渐减弱"),
            new PatternInfo(Pattern.Triangle, "往复", "匀速增强，再匀速减弱"),
            new PatternInfo(Pattern.StepUp, "阶梯上升", "分五档逐步增强"),
            new PatternInfo(Pattern.StepDown, "阶梯下降", "分五档逐步减弱"),
            new PatternInfo(Pattern.Alternate, "左右交替", "左、右马达各震动半拍"),
            new PatternInfo(Pattern.Crossfade, "左右交融", "一侧增强时，另一侧逐渐减弱"),
            new PatternInfo(Pattern.Random, "随机变化", "每拍换一个强度，过渡保持平滑"),
            new PatternInfo(Pattern.Melody, "马达旋律", "")
        };
    }

    public sealed class Settings
    {
        public readonly double Left, Right, Period;
        public readonly Pattern Pattern;
        public readonly Melody Melody;
        public bool Paused { get { return Pattern == Pattern.Melody && double.IsPositiveInfinity(Period); } }
        public Settings(double left, double right, double period, Pattern pattern, Melody melody = null)
        {
            Left = Clamp(left, 0, 1);
            Right = Clamp(right, 0, 1);
            // An infinite beat period is the explicit 0 BPM (paused) setting.
            Period = pattern == Pattern.Melody && double.IsPositiveInfinity(period) ? period : Clamp(period, PatternLibrary.MinimumPeriod(pattern), pattern == Pattern.Melody ? 60 : 5);
            Pattern = pattern;
            Melody = melody ?? MelodyLibrary.All[0];
        }
        private static double Clamp(double value, double low, double high)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return low;
            return Math.Max(low, Math.Min(high, value));
        }
        public void Sample(double elapsed, out ushort left, out ushort right)
        {
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed)) elapsed = 0;
            double cycles = Math.Max(0, elapsed) / Period;
            SampleCycles(cycles, out left, out right);
        }
        public void SampleCycles(double cycles, out ushort left, out ushort right)
        {
            if (Paused) { left = right = 0; return; }
            if (double.IsNaN(cycles) || double.IsInfinity(cycles) || cycles < 0) cycles = 0;
            double phase = cycles;
            phase -= Math.Floor(phase);
            double l = 1, r = 1;
            switch (Pattern)
            {
                case Pattern.Pulse: l = r = phase < 0.5 ? 1 : 0; break;
                case Pattern.Breathe: l = r = (1 - Math.Cos(2 * Math.PI * phase)) / 2; break;
                case Pattern.DoublePulse:
                    l = r = phase < 0.12 || (phase >= 0.24 && phase < 0.36) ? 1 : 0;
                    break;
                case Pattern.Alternate: l = phase < 0.5 ? 1 : 0; r = 1 - l; break;
                case Pattern.TriplePulse:
                    l = r = phase < 0.10 || (phase >= 0.20 && phase < 0.30) || (phase >= 0.40 && phase < 0.50) ? 1 : 0;
                    break;
                case Pattern.Heartbeat:
                    l = r = Math.Max(Bell(phase, 0.02, 0.18), 0.6 * Bell(phase, 0.25, 0.16));
                    break;
                case Pattern.RampUp: l = r = phase; break;
                case Pattern.RampDown: l = r = 1 - phase; break;
                case Pattern.Triangle: l = r = 1 - Math.Abs(2 * phase - 1); break;
                case Pattern.Crossfade:
                    l = (1 - Math.Cos(2 * Math.PI * phase)) / 2; r = 1 - l;
                    break;
                case Pattern.Burst:
                    l = r = phase < 0.5 && phase % 0.125 < 0.0625 ? 1 : 0;
                    break;
                case Pattern.StepUp: l = r = (Math.Floor(phase * 5) + 1) / 5; break;
                case Pattern.StepDown: l = r = (5 - Math.Floor(phase * 5)) / 5; break;
                case Pattern.Random:
                    double cycle = Math.Floor(cycles);
                    double smooth = phase * phase * (3 - 2 * phase);
                    l = r = Noise(cycle) * (1 - smooth) + Noise(cycle + 1) * smooth;
                    break;
                case Pattern.Wave: l = r = 0.3 + 0.7 * (1 - Math.Cos(2 * Math.PI * phase)) / 2; break;
                case Pattern.Melody: Melody.Sample(cycles, out l, out r); break;
            }
            left = (ushort)Math.Round(Left * Clamp(l, 0, 1) * ushort.MaxValue);
            right = (ushort)Math.Round(Right * Clamp(r, 0, 1) * ushort.MaxValue);
        }
        private static double Bell(double phase, double start, double width)
        {
            if (phase <= start || phase >= start + width) return 0;
            double sine = Math.Sin(Math.PI * (phase - start) / width);
            return sine * sine;
        }
        // Stateless pseudo-random envelope: preview and playback sample the same curve.
        private static double Noise(double cycle)
        {
            double value = Math.Sin((cycle + 1) * 127.1 + 311.7) * 43758.5453;
            return 0.15 + 0.85 * (value - Math.Floor(value));
        }
    }

    // The session is deterministic; the host supplies a monotonic clock.
    // All calls are serialized by RumbleHost. Tests use a fake controller.
    public sealed class RumbleSession
    {
        public const int MaximumDurationSeconds = 86400;
        private readonly IController controller;
        private readonly HashSet<int> pendingZero = new HashSet<int>();
        private double started, lastSend, lastRetry;
        private double melodyBeat, positionTime;
        private double? deadline;
        private int index;
        private string stopButtonLabel = "B";
        public bool Active { get; private set; }
        public string Message { get; private set; }
        public ushort Left { get; private set; }
        public ushort Right { get; private set; }
        public Settings Settings { get; set; }
        public void Configure(Settings settings, double now)
        {
            AdvancePosition(now);
            if (Settings.Pattern != settings.Pattern || Settings.Melody != settings.Melody) melodyBeat = 0;
            Settings = settings;
        }
        private void AdvancePosition(double now)
        {
            if (Active && Settings.Pattern == Pattern.Melody && !Settings.Paused)
                melodyBeat += Math.Max(0, now - positionTime) / Settings.Period;
            positionTime = now;
        }
        public double MelodyBeat(double now)
        {
            if (!Active) return 0;
            return melodyBeat + (Settings.Pattern == Pattern.Melody && !Settings.Paused ? Math.Max(0, now - positionTime) / Settings.Period : 0);
        }
        public RumbleSession(IController controller)
        {
            this.controller = controller;
            Settings = new Settings(0.2, 0.2, 1.5, Pattern.Breathe);
            Message = "待机";
        }
        // A missing duration means unlimited playback; zero/invalid numbers remain errors.
        public bool Start(int selected, double? seconds, double now)
        {
            if (Active) Stop("已停止上一段播放");
            if (selected < 0 || (seconds.HasValue && (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || seconds.Value < 1 || seconds.Value > MaximumDurationSeconds)))
            {
                Message = "请选择手柄，并设置 1–86400 秒或无限播放";
                return false;
            }
            ushort buttons;
            controller.Update();
            ControllerDevice device = controller.GetDevice(selected);
            if (device == null) { Message = "未检测到所选手柄，连接后重新开始"; return false; }
            if (!device.CanRumble) { Message = "此设备或驱动未提供震动支持"; return false; }
            stopButtonLabel = device.StopButtonLabel;
            if (!controller.Read(selected, out buttons))
            {
                Message = "未检测到所选手柄 · 连接后重新点击开始";
                return false;
            }
            if ((buttons & 0x2000) != 0)
            {
                Message = "请先松开手柄 " + stopButtonLabel + " 键，再点击开始";
                return false;
            }
            index = selected;
            // Verify that a zero command is accepted before any nonzero output.
            if (controller.Write(index, 0, 0) != 0)
            {
                Message = "驱动未接受震动指令：" + controller.LastError;
                pendingZero.Add(index);
                return false;
            }
            pendingZero.Remove(index);
            started = now;
            melodyBeat = 0; positionTime = now;
            deadline = seconds.HasValue ? now + seconds.Value : (double?)null;
            lastSend = now - 1;
            Active = true;
            Message = "播放中";
            return true;
        }
        public double? Remaining(double now) { return !Active ? 0 : deadline.HasValue ? Math.Max(0, deadline.Value - now) : (double?)null; }
        public double Elapsed(double now) { return Active ? Math.Max(0, now - started) : 0; }
        public void Stop(string reason)
        {
            bool wasActive = Active;
            Active = false;
            Left = Right = 0;
            Message = reason;
            if (wasActive && controller.Write(index, 0, 0) != 0) pendingZero.Add(index);
        }
        public void Tick(double now)
        {
            controller.Update();
            if (now - lastRetry >= 0.5)
            {
                lastRetry = now;
                foreach (int device in new List<int>(pendingZero))
                    if (controller.Write(device, 0, 0) == 0) pendingZero.Remove(device);
            }
            if (!Active) return;
            if (deadline.HasValue && now >= deadline.Value) { Stop("定时结束 · 已停止"); return; }
            ushort buttons;
            if (!controller.Read(index, out buttons)) { Stop("手柄已断开 · 重连后需手动开始"); return; }
            if ((buttons & 0x2000) != 0) { Stop("手柄 " + stopButtonLabel + " 键 · 已停止"); return; }
            ushort left, right;
            AdvancePosition(now);
            if (Settings.Pattern == Pattern.Melody) Settings.SampleCycles(melodyBeat, out left, out right);
            else Settings.Sample(now - started, out left, out right);
            Message = Settings.Paused ? "0 BPM · 已暂停，调高速度继续" : "播放中";
            if (left == Left && right == Right && now - lastSend < 0.25) return;
            uint error = controller.Write(index, left, right);
            if (error != 0) { string reason = controller.LastError; Stop("震动指令失败 · " + reason); return; }
            Left = left; Right = right; lastSend = now;
        }
    }

    public sealed class Snapshot
    {
        public bool Active;
        public string Message;
        public double? Remaining;
        public double Elapsed;
        public double MelodyBeat;
        public bool Paused;
        public ushort Left, Right;
    }

    public sealed class RumbleHost : IDisposable
    {
        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly RumbleSession session;
        private readonly Thread worker;
        private volatile bool quitting;
        public RumbleHost(IController controller)
        {
            session = new RumbleSession(controller);
            worker = new Thread(Loop) { IsBackground = true, Name = "Rumble output" };
            worker.Start();
        }
        private void Loop()
        {
            while (!quitting)
            {
                lock (gate)
                {
                    try { session.Tick(clock.Elapsed.TotalSeconds); }
                    catch (Exception) { try { session.Stop("控制器访问异常 · 已停止"); } catch { } }
                }
                Thread.Sleep(20);
            }
        }
        public void Configure(Settings settings) { lock (gate) session.Configure(settings, clock.Elapsed.TotalSeconds); }
        public void Start(int index, double? duration) { lock (gate) session.Start(index, duration, clock.Elapsed.TotalSeconds); }
        public void Stop(string reason) { lock (gate) session.Stop(reason); }
        public Snapshot Read()
        {
            lock (gate)
            {
                double now = clock.Elapsed.TotalSeconds;
                // UI reads are observational. Hidden/occluded windows need not renew output.
                return new Snapshot { Active = session.Active, Message = session.Message, Remaining = session.Remaining(now), Elapsed = session.Elapsed(now), MelodyBeat = session.MelodyBeat(now), Paused = session.Settings.Paused, Left = session.Left, Right = session.Right };
            }
        }
        public void Dispose()
        {
            quitting = true;
            lock (gate) session.Stop("已关闭");
            worker.Join(1000);
        }
    }
}

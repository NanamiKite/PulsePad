using System;
using System.Collections.Generic;
using XboxRumble;

internal sealed class FakeController : IController
{
    public sealed class Output { public int Index; public ushort Left, Right; }
    public bool Connected = true;
    public bool FailNonzero;
    public ushort Buttons;
    public string BackendName { get { return "Fake controllers"; } }
    public string LastError { get { return "Simulated error"; } }
    public string Notice { get { return ""; } }
    public readonly Dictionary<int, ControllerDevice> Devices = new Dictionary<int, ControllerDevice>();
    public FakeController()
    {
        for (int i = 0; i < 4; i++) Devices.Add(i, new ControllerDevice(i, "测试手柄 " + i, "Xbox", "有线", RumbleCapability.DualChannel, "B"));
    }
    public ControllerDevice[] Enumerate()
    {
        List<ControllerDevice> list = new List<ControllerDevice>();
        if (Connected) list.AddRange(Devices.Values);
        list.Sort(delegate(ControllerDevice a, ControllerDevice b) { return a.Id.CompareTo(b.Id); });
        return list.ToArray();
    }
    public ControllerDevice GetDevice(int id) { ControllerDevice device; return Connected && Devices.TryGetValue(id, out device) ? device : null; }
    public void Update() { }
    public void Dispose() { }
    public List<Output> Outputs = new List<Output>();
    public bool Read(int index, out ushort buttons) { buttons = Buttons; return GetDevice(index) != null; }
    public uint Write(int index, ushort left, ushort right)
    {
        Outputs.Add(new Output { Index = index, Left = left, Right = right });
        return !Connected ? 1167u : (FailNonzero && (left != 0 || right != 0) ? 31u : 0u);
    }
    public bool LastIsZero()
    {
        return Outputs.Count > 0 && Outputs[Outputs.Count - 1].Left == 0 && Outputs[Outputs.Count - 1].Right == 0;
    }
}

internal static class RumbleTests
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static RumbleSession Session(FakeController fake)
    {
        RumbleSession session = new RumbleSession(fake);
        session.Settings = new Settings(0.4, 0.6, 1, Pattern.Constant);
        return session;
    }
    private static void TestPatterns()
    {
        ushort l, r;
        new Settings(1, 0, 1, Pattern.Constant).Sample(0, out l, out r);
        Assert(l == 65535 && r == 0, "Independent motor endpoints");
        new Settings(1, 1, 1, Pattern.Pulse).Sample(0.49, out l, out r);
        Assert(l == 65535 && r == 65535, "Pulse on");
        new Settings(1, 1, 1, Pattern.Pulse).Sample(0.5, out l, out r);
        Assert(l == 0 && r == 0, "Pulse off");
        new Settings(1, 1, 1, Pattern.Breathe).Sample(0.5, out l, out r);
        Assert(l == 65535 && r == 65535, "Breathe peak");
        new Settings(1, 1, 1, Pattern.Breathe).Sample(1, out l, out r);
        Assert(l == 0 && r == 0, "Breathe cycle ends at zero");
        new Settings(1, 1, 1, Pattern.DoublePulse).Sample(0.3, out l, out r);
        Assert(l == 65535 && r == 65535, "Second pulse");
        new Settings(1, 1, 1, Pattern.DoublePulse).Sample(0.4, out l, out r);
        Assert(l == 0 && r == 0, "Double pulse rest");
        new Settings(1, 1, 1, Pattern.Alternate).Sample(0.7, out l, out r);
        Assert(l == 0 && r == 65535, "Alternating right half");
        foreach (Pattern pattern in Enum.GetValues(typeof(Pattern)))
        {
            Settings settings = new Settings(0.2, 0.6, 0.2, pattern);
            for (int i = 0; i < 300; i++)
            {
                settings.Sample(i * 0.013, out l, out r);
                Assert(l <= 13107 && r <= 39321, "Pattern must not exceed requested strength");
            }
        }
        Settings invalid = new Settings(double.NaN, double.PositiveInfinity, double.NaN, Pattern.Constant);
        invalid.Sample(0, out l, out r);
        Assert(l == 0 && r == 0 && invalid.Period == 0.2, "Invalid settings cannot overflow motors");
        Settings clamp = new Settings(-1, 2, 100, Pattern.Constant);
        clamp.Sample(0, out l, out r);
        Assert(l == 0 && r == 65535 && clamp.Period == 5, "Settings clamped");
    }
    private static void TestLifecycle()
    {
        FakeController fake = new FakeController();
        RumbleSession session = Session(fake);
        session.Tick(0);
        Assert(!session.Active && fake.Outputs.Count == 0, "Opening app must never energize a motor");
        Assert(session.Start(2, 2, 0), "Connected slot can start");
        Assert(fake.LastIsZero(), "Initial command is zero");
        session.Tick(0.1);
        Assert(!fake.LastIsZero() && fake.Outputs[fake.Outputs.Count - 1].Index == 2, "Only selected slot receives output");
        session.Tick(2);
        Assert(!session.Active && fake.LastIsZero(), "Deadline stops exactly at limit");
        int count = fake.Outputs.Count;
        session.Tick(3);
        Assert(fake.Outputs.Count == count, "Timer expiry cannot restart itself");
        session.Start(2, 10, 3);
        session.Tick(3.1);
        session.Stop("stop button");
        Assert(!session.Active && fake.LastIsZero(), "Manual stop sends zero");
        count = fake.Outputs.Count;
        session.Settings = new Settings(1, 1, 0.2, Pattern.Pulse);
        session.Tick(4);
        Assert(fake.Outputs.Count == count, "Editing idle controls must not start output");
        Assert(!session.Start(-1, 10, 4) && !session.Start(4, 10, 4), "Invalid slots rejected");
        Assert(!session.Start(0, double.NaN, 4) && !session.Start(0, 86401, 4), "Invalid duration rejected");
    }
    private static double Strength(Pattern pattern, double time)
    {
        ushort left, right;
        new Settings(1, 1, 1, pattern).Sample(time, out left, out right);
        return left / 65535.0;
    }
    private static void TestExpandedPatterns()
    {
        Assert(PatternLibrary.All.Length == Enum.GetValues(typeof(Pattern)).Length, "Every pattern has a UI entry");
        HashSet<Pattern> listed = new HashSet<Pattern>();
        foreach (PatternInfo info in PatternLibrary.All) Assert(listed.Add(info.Pattern), "Mode list contains no duplicates");
        Assert(Strength(Pattern.TriplePulse, 0.05) == 1 && Strength(Pattern.TriplePulse, 0.25) == 1 && Strength(Pattern.TriplePulse, 0.45) == 1, "Triple pulse has three on segments");
        Assert(Strength(Pattern.TriplePulse, 0.15) == 0 && Strength(Pattern.TriplePulse, 0.6) == 0, "Triple pulse has rests");
        Assert(Strength(Pattern.Heartbeat, 0.11) > 0.99 && Math.Abs(Strength(Pattern.Heartbeat, 0.33) - 0.6) < 0.001 && Strength(Pattern.Heartbeat, 0.7) == 0, "Heartbeat has a strong beat, soft beat, and rest");
        Assert(Math.Abs(Strength(Pattern.RampUp, 0.25) - 0.25) < 0.001 && Math.Abs(Strength(Pattern.RampDown, 0.25) - 0.75) < 0.001, "Ramps have opposite slopes");
        Assert(Math.Abs(Strength(Pattern.Triangle, 0.25) - 0.5) < 0.001 && Strength(Pattern.Triangle, 0.5) == 1, "Triangle ramps up and down");
        Assert(Strength(Pattern.Burst, 0.01) == 1 && Strength(Pattern.Burst, 0.08) == 0 && Strength(Pattern.Burst, 0.39) == 1 && Strength(Pattern.Burst, 0.6) == 0, "Burst rhythm and long rest");
        Assert(Math.Abs(Strength(Pattern.StepUp, 0.01) - 0.2) < 0.001 && Strength(Pattern.StepUp, 0.85) == 1, "Five ascending steps");
        Assert(Strength(Pattern.StepDown, 0.01) == 1 && Math.Abs(Strength(Pattern.StepDown, 0.85) - 0.2) < 0.001, "Five descending steps");
        Assert(Math.Abs(Strength(Pattern.Wave, 0) - 0.3) < 0.001 && Strength(Pattern.Wave, 0.5) == 1, "Wave has a nonzero floor");
        Assert(Math.Abs(Strength(Pattern.Random, 0.99999) - Strength(Pattern.Random, 1.00001)) < 0.001, "Random transitions are continuous across cycles");
        Assert(Strength(Pattern.Random, 0.15) == Strength(Pattern.Random, 0.15) && Strength(Pattern.Random, 0.15) != Strength(Pattern.Random, 3.15), "Random preview is deterministic but varies between cycles");
        for (int i = 0; i < 100; i++)
        {
            ushort left, right;
            new Settings(1, 1, 1, Pattern.Crossfade).Sample(i * 0.01, out left, out right);
            Assert(Math.Abs(left + right - 65535) <= 1, "Crossfade keeps combined output constant");
        }
        Assert(new Settings(1, 1, 0.2, Pattern.Burst).Period == 0.8, "Fast bursts cannot request pulses shorter than the output tick");
    }
    private static void TestStopPaths()
    {
        FakeController fake = new FakeController();
        RumbleSession session = Session(fake);
        session.Start(0, 20, 0);
        session.Tick(0.1);
        session.Tick(1.51);
        Assert(session.Active && !fake.LastIsZero(), "Playback is independent of UI refresh after 1.5 seconds");
        session.Start(0, 20, 2);
        session.Tick(2.1);
        fake.Buttons = 0x2000;
        session.Tick(2.2);
        Assert(!session.Active && fake.LastIsZero(), "B button stops output");
        Assert(!session.Start(0, 20, 2.3), "Held B must prevent start");
        fake.Buttons = 0;
        session.Start(0, 20, 3);
        session.Tick(3.1);
        fake.Connected = false;
        session.Tick(3.2);
        Assert(!session.Active && fake.LastIsZero(), "Disconnect stops logical playback and attempts zero");
        fake.Connected = true;
        session.Tick(4);
        Assert(!session.Active && fake.LastIsZero(), "Reconnect clears residual output without resuming");
        int count = fake.Outputs.Count;
        session.Tick(5);
        Assert(fake.Outputs.Count == count, "Successful reconnect zero is not repeated forever");
        fake.FailNonzero = true;
        session.Start(0, 20, 6);
        session.Tick(6.1);
        Assert(!session.Active && fake.LastIsZero(), "Write failure triggers stop");
        fake.FailNonzero = false;
        fake.Connected = false;
        count = fake.Outputs.Count;
        Assert(!session.Start(0, 20, 7) && fake.Outputs.Count == count, "Disconnected start cannot send nonzero");
    }
    private static void TestSwitchAndLiveChanges()
    {
        FakeController fake = new FakeController();
        RumbleSession session = Session(fake);
        session.Start(0, 5, 0);
        session.Tick(0.1);
        int count = fake.Outputs.Count;
        session.Start(1, 5, 0.2);
        Assert(fake.Outputs[count].Index == 0 && fake.Outputs[count].Left == 0 && fake.Outputs[count].Right == 0, "Switch zeros previous slot first");
        session.Settings = new Settings(0, 0.3, 1, Pattern.Constant);
        session.Tick(0.3);
        FakeController.Output output = fake.Outputs[fake.Outputs.Count - 1];
        Assert(output.Index == 1 && output.Left == 0 && output.Right == (ushort)Math.Round(0.3 * 65535), "Live changes apply independently");
        session.Tick(5.2);
        Assert(!session.Active && fake.LastIsZero(), "Live changes do not extend deadline");
    }
    private static void TestDeviceCapabilities()
    {
        FakeController fake = new FakeController();
        fake.Devices.Add(101, new ControllerDevice(101, "DualSense", "PlayStation", "有线", RumbleCapability.DualChannel, "○"));
        fake.Devices.Add(900, new ControllerDevice(900, "USB pad", "通用", "有线", RumbleCapability.None, ""));
        RumbleSession session = Session(fake);
        Assert(session.Start(101, 20, 0), "Device IDs are not limited to four XInput slots");
        session.Tick(0.1);
        Assert(fake.Outputs[fake.Outputs.Count - 1].Index == 101, "Output uses stable ID, not list index");
        fake.Buttons = 0x2000;
        session.Tick(0.2);
        Assert(!session.Active && session.Message.Contains("○") && fake.LastIsZero(), "Stop message uses controller-specific label");
        fake.Buttons = 0;
        int count = fake.Outputs.Count;
        Assert(!session.Start(900, 20, 1) && fake.Outputs.Count == count, "Unsupported devices cannot send output");
        fake.Devices.Remove(101);
        Assert(!session.Start(101, 20, 1), "Stale disconnected IDs cannot start another device");
    }
    private static void TestMelodies()
    {
        ushort l, r, nextL, nextR;
        Settings twinkle = new Settings(0.3, 0.7, 0.5, Pattern.Melody);
        twinkle.Sample(0.1, out l, out r);
        twinkle.Sample(1.1, out nextL, out nextR);
        Assert(r > 0 && nextR > r, "Score ascending fifth increases motor speed command");
        twinkle.Sample(0.45, out l, out r);
        Assert(l == 0 && r == 0, "Separation between repeated notes is silent");
        twinkle.Sample(24.5, out l, out r);
        Assert(l == 0 && r == 0, "End of score has an explicit rest");
        twinkle.Sample(0.1, out l, out r);
        twinkle.Sample(25.1, out nextL, out nextR);
        Assert(l == nextL && r == nextR, "Score loops deterministically for preview and output");
        new Settings(0.3, 0.7, 1, Pattern.Melody).Sample(2.2, out nextL, out nextR);
        twinkle.Sample(1.1, out l, out r);
        Assert(l == nextL && r == nextR, "Tempo scales note timing");
        foreach (Melody melody in MelodyLibrary.All)
        {
            Settings setting = new Settings(0, 0.42, 60.0 / 300, Pattern.Melody, melody);
            for (int i = 0; i < 600; i++)
            {
                setting.Sample(i * 0.033, out l, out r);
                Assert(l == 0 && r <= Math.Round(0.42 * 65535), "Melody respects independent user intensity limits");
            }
            setting.Sample(double.NaN, out l, out r);
            Assert(l == 0 && r <= Math.Round(0.42 * 65535), "Invalid sample time remains bounded");
        }
        Assert(new Settings(1, 1, 0, Pattern.Melody).Period == 0.2 && new Settings(1, 1, 100, Pattern.Melody).Period == 60, "Motor melody accepts 1-300 BPM and bounds invalid periods");
        FakeController fake = new FakeController();
        RumbleSession session = Session(fake); session.Settings = twinkle;
        Assert(session.Start(0, 1, 0), "Melody uses normal start guard");
        session.Tick(0.1); Assert(!fake.LastIsZero(), "Melody reaches output");
        session.Tick(1); Assert(!session.Active && fake.LastIsZero(), "Timer interrupts melody within the score");
    }
    private static void TestTempoPause()
    {
        Melody song = new Melody("Tempo test", new[] { 60,72,67 }, new double[] { 1,1,1 }, 120, "", 1);
        Settings paused = new Settings(1, 1, double.PositiveInfinity, Pattern.Melody, song);
        Settings normal = new Settings(1, 1, 1, Pattern.Melody, song);
        Settings fast = new Settings(1, 1, 0.2, Pattern.Melody, song);
        ushort l, r; paused.Sample(100, out l, out r);
        Assert(paused.Paused && l == 0 && r == 0, "Zero BPM preview is silent");
        FakeController fake = new FakeController(); RumbleSession session = Session(fake);
        session.Configure(paused, 10); Assert(session.Start(0, null, 10), "Can start at zero BPM");
        session.Tick(20);
        Assert(fake.LastIsZero() && session.MelodyBeat(20) == 0 && session.Active, "Starting paused never advances or emits nonzero output");
        session.Configure(normal, 20); session.Tick(20.25);
        Assert(session.Right == 32768 && Math.Abs(session.MelodyBeat(20.25) - 0.25) < 1e-9, "Resume starts at first note");
        session.Configure(paused, 20.3); session.Tick(40);
        Assert(fake.LastIsZero() && Math.Abs(session.MelodyBeat(40) - 0.3) < 1e-9, "Pause includes partial tick and freezes score position");
        session.Configure(fast, 40); session.Tick(40.1);
        Assert(session.Right == 32768 && Math.Abs(session.MelodyBeat(40.1) - 0.8) < 1e-9, "Resume at 300 BPM continues the same note without jumping");
        session.Tick(40.16);
        Assert(session.Right == 65535, "300 BPM advances to next note after remaining fraction");
        session.Configure(new Settings(1, 1, 60, Pattern.Melody, song), 40.16); session.Tick(41.16);
        Assert(Math.Abs(session.MelodyBeat(41.16) - (1.1 + 1.0 / 60)) < 1e-9 && session.Right == 65535, "Live change to 1 BPM preserves musical position");
        session.Configure(paused, 42); fake.Buttons = 0x2000; session.Tick(42.02);
        Assert(!session.Active && fake.LastIsZero(), "Controller stop works while paused");
        fake.Buttons = 0; session.Start(0, 2, 50); session.Tick(51);
        Assert(session.Active && session.Remaining(51) == 1 && fake.LastIsZero(), "Finite timer continues during zero BPM pause");
        session.Tick(52); Assert(!session.Active && fake.LastIsZero(), "Finite timer expires during pause");
    }
    private static void TestUnlimitedDuration()
    {
        FakeController fake = new FakeController();
        RumbleSession session = Session(fake);
        foreach (double invalid in new[] { -1.0, 0, 0.5, 86401, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert(!session.Start(0, invalid, 0), "Invalid durations cannot silently turn into unlimited playback");
        Assert(fake.Outputs.Count == 0, "Invalid duration does not send output");
        Assert(session.Start(0, null, 0), "An explicit missing duration starts unlimited playback");
        session.Tick(864000);
        Assert(session.Active && !session.Remaining(864000).HasValue && !fake.LastIsZero(), "Unlimited playback has no artificial deadline or UI heartbeat");
        fake.Buttons = 0x2000; session.Tick(864000.1);
        Assert(!session.Active && fake.LastIsZero(), "Controller stop still works after long unlimited playback");
        fake.Buttons = 0;
        Assert(session.Start(0, null, 864001), "Unlimited can restart explicitly");
        session.Tick(864001.1); fake.Connected = false; session.Tick(864001.2);
        Assert(!session.Active, "Disconnect stops unlimited playback");
        fake.Connected = true; session.Tick(864002);
        Assert(!session.Active && fake.LastIsZero(), "Reconnect does not restart unlimited playback");
        Assert(session.Start(0, 86400, 864010), "A full-day timer is valid");
        session.Tick(950409.9);
        Assert(session.Active && session.Remaining(950409.9).Value < 0.11, "Long finite timer remains finite");
        session.Tick(950410);
        Assert(!session.Active && fake.LastIsZero(), "Long timer stops at its deadline without UI updates");
    }
    public static int Main()
    {
        try
        {
            TestPatterns(); TestExpandedPatterns(); TestLifecycle(); TestStopPaths(); TestSwitchAndLiveChanges(); TestDeviceCapabilities(); TestMelodies(); TestTempoPause(); TestUnlimitedDuration();
            Console.WriteLine("PASS: " + checks + " assertions; patterns, limits, lifecycle, timeout, B, disconnect, write errors, slot switching.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

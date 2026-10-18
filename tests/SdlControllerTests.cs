using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using XboxRumble;

// Exercise the shipped native libraries without sending rumble to physical devices.
internal static class SdlControllerTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualDesc
    {
        public uint Version;
        public ushort Type, Padding, Vendor, Product, Axes, Buttons, Balls, Hats, Touchpads, Sensors, Padding1, Padding2;
        public uint ButtonMask, AxisMask;
        public IntPtr Name, TouchpadDescriptions, SensorDescriptions, UserData;
        public IntPtr Update, SetPlayerIndex, Rumble, RumbleTriggers, SetLED, SendEffect, SetSensorsEnabled, Cleanup;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool RumbleCallback(IntPtr userData, ushort low, ushort high);
    [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_AttachVirtualJoystick(ref VirtualDesc description);
    [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_DetachVirtualJoystick(uint id);
    [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetJoystickFromID(uint id);
    [DllImport("SDL3.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_SetJoystickVirtualButton(IntPtr joystick, int button, [MarshalAs(UnmanagedType.I1)] bool pressed);

    private sealed class VirtualPad
    {
        public int Id, Calls;
        public ushort Low, High;
        public bool Fail, Detached;
        public IntPtr Name;
        public RumbleCallback Callback;
        public VirtualPad(string name, ushort vendor, ushort product, bool rumble, ushort kind)
        {
            Name = Marshal.StringToHGlobalAnsi(name);
            Callback = delegate(IntPtr unused, ushort low, ushort high)
            {
                Calls++;
                if (Fail && (low != 0 || high != 0)) return false;
                Low = low; High = high; return true;
            };
            VirtualDesc desc = new VirtualDesc
            {
                Version = (uint)Marshal.SizeOf(typeof(VirtualDesc)), Type = kind,
                Vendor = vendor, Product = product, Axes = 6, Buttons = 15,
                ButtonMask = 0x7fff, AxisMask = 0x3f, Name = Name,
                Rumble = rumble ? Marshal.GetFunctionPointerForDelegate(Callback) : IntPtr.Zero
            };
            Id = (int)SDL_AttachVirtualJoystick(ref desc);
            Assert(Id != 0, "Attach virtual joystick: " + SdlNative.Error);
        }
        public bool Zero { get { return Low == 0 && High == 0; } }
        public void Button(int button, bool pressed)
        {
            Assert(SDL_SetJoystickVirtualButton(SDL_GetJoystickFromID((uint)Id), button, pressed), "Set virtual button");
        }
        public void Detach()
        {
            if (Detached) return;
            Assert(SDL_DetachVirtualJoystick((uint)Id), "Detach virtual joystick");
            Detached = true;
        }
    }
    private static int checks;
    private static void Assert(bool value, string reason)
    {
        checks++;
        if (!value) throw new Exception(reason);
    }
    [STAThread]
    private static int Main()
    {
        List<VirtualPad> pads = new List<VirtualPad>();
        try
        {
            Assert(Marshal.SizeOf(typeof(VirtualDesc)) == (IntPtr.Size == 8 ? 136 : 84), "SDL descriptor ABI size");
            using (SdlController controller = new SdlController())
            {
                try
                {
                    VirtualPad xbox = new VirtualPad("Test Xbox", 0x045e, 0x0b13, true, 1); pads.Add(xbox);
                    VirtualPad ps = new VirtualPad("Test DualSense", 0x054c, 0x0ce6, true, 1); pads.Add(ps);
                    VirtualPad ns = new VirtualPad("Test Switch Pro", 0x057e, 0x2009, true, 1); pads.Add(ns);
                    VirtualPad silent = new VirtualPad("Test no rumble", 0, 0, false, 1); pads.Add(silent);
                    VirtualPad wheel = new VirtualPad("Test excluded wheel", 0, 0, true, 2); pads.Add(wheel);
                    controller.Enumerate();
                    Assert(controller.GetDevice(xbox.Id).Rumble == RumbleCapability.DualChannel, "Native rumble property");
                    Assert(controller.GetDevice(ps.Id).Family == "PlayStation", "PS type from native mapping");
                    Assert(controller.GetDevice(ps.Id).StopButtonLabel == "○", "PS circle stop label");
                    Assert(controller.GetDevice(ns.Id).Family == "Nintendo Switch", "NS type from native mapping");
                    Assert(controller.GetDevice(ns.Id).StopButtonLabel == "B", "NS B stop label");
                    Assert(!controller.GetDevice(silent.Id).CanRumble, "Missing native capability stays unsupported");
                    Assert(controller.GetDevice(wheel.Id) == null, "Do not list wheels as gamepads");
                    Assert(xbox.Calls == 0 && ps.Calls == 0 && ns.Calls == 0, "Discovery never requests rumble");
                    Assert(controller.Write(ps.Id, 12345, 54321) == 0, "Native write success");
                    Assert(ps.Low == 12345 && ps.High == 54321, "Exact two-channel values reach SDL callback");
                    Assert(xbox.Calls == 0 && ns.Calls == 0, "Non-selected devices untouched");
                    Assert(controller.Write(ps.Id, 0, 0) == 0 && ps.Zero, "Explicit stop reaches native callback");
                    Assert(controller.Write(silent.Id, 1, 1) != 0 && silent.Calls == 0, "Unsupported write rejected");

                    ushort buttons;
                    ps.Button(1, true); controller.Update();
                    Assert(controller.Read(ps.Id, out buttons) && buttons == 0x2000, "PS east/circle maps to stop");
                    ps.Button(1, false); controller.Update();
                    ns.Button(0, true); controller.Update();
                    Assert(controller.Read(ns.Id, out buttons) && buttons == 0x2000, "Nintendo south/B maps to stop");
                    ns.Button(0, false); ns.Button(1, true); controller.Update();
                    Assert(controller.Read(ns.Id, out buttons) && buttons == 0, "Nintendo east/A is not B");
                    ns.Button(1, false); controller.Update();

                    RumbleSession session = new RumbleSession(controller);
                    session.Settings = new Settings(0.2, 0.4, 1, Pattern.Constant);
                    Assert(session.Start(ns.Id, 10, 0), "Start native Nintendo device");
                    session.Tick(0.1);
                    Assert(ns.Low == 13107 && ns.High == 26214, "Engine routes output through native backend");
                    ns.Button(0, true); session.Tick(0.2);
                    Assert(!session.Active && ns.Zero && session.Message.Contains("B"), "Nintendo B stops engine and native output");
                    Assert(!session.Start(ns.Id, 10, 0.3), "Held native stop button blocks restart");
                    ns.Button(0, false); controller.Update();
                    Assert(session.Start(ps.Id, 10, 1), "Start before simulated driver failure");
                    session.Tick(1.1);
                    ps.Fail = true;
                    session.Settings = new Settings(0.3, 0.5, 1, Pattern.Constant);
                    session.Tick(1.2);
                    Assert(!session.Active && ps.Zero, "Native output failure stops and zeros");
                    ps.Fail = false;
                    Assert(session.Start(ps.Id, 10, 2), "Start before hot unplug");
                    session.Tick(2.1); ps.Detach(); session.Tick(2.2);
                    Assert(!session.Active && controller.GetDevice(ps.Id) == null, "Native disconnect stops session");
                    VirtualPad replacement = new VirtualPad("Test replacement DualSense", 0x054c, 0x0ce6, true, 1); pads.Add(replacement);
                    controller.Enumerate(); session.Tick(2.3);
                    Assert(replacement.Id != ps.Id && replacement.Calls == 0, "New instance never inherits old output");
                    Assert(controller.Write(ps.Id, 2000, 2000) != 0 && replacement.Calls == 0, "Stale ID cannot target replacement");
                    Assert(!session.Start(silent.Id, 10, 3), "Engine rejects unsupported native device");
                    Assert(controller.Write(xbox.Id, 11111, 22222) == 0, "Start finite native request");
                    Thread.Sleep(820); controller.Update();
                    Assert(xbox.Zero, "SDL expires request when update loop runs");
                    using (RumbleHost background = new RumbleHost(controller))
                    {
                        background.Configure(new Settings(0.2, 0.4, 1, Pattern.Constant));
                        background.Start(xbox.Id, null);
                        Thread.Sleep(1800); // No UI callbacks, longer than both old UI and native expiry windows.
                        Assert(background.Read().Active && xbox.Low == 13107 && xbox.High == 26214, "Worker renews native rumble without foreground/UI callbacks");
                        background.Stop("test stop");
                        Assert(xbox.Zero, "Unlimited native output stops explicitly");
                        background.Start(xbox.Id, 1);
                        Thread.Sleep(1200);
                        Assert(!background.Read().Active && xbox.Zero, "Background timed playback expires without UI callbacks");
                    }
                    Assert(controller.Write(replacement.Id, 4321, 1234) == 0, "Start output before backend disposal");
                    controller.Dispose();
                    Assert(replacement.Zero, "Backend disposal zeros previously written device");
                    foreach (VirtualPad pad in pads) pad.Detached = true; // SDL_Quit detached them.
                }
                finally
                {
                    foreach (VirtualPad pad in pads) pad.Detach();
                }
            }
            Console.WriteLine("PASS: " + checks + " SDL native assertions (" + (IntPtr.Size * 8) + "-bit); physical devices never selected for output.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (VirtualPad pad in pads) { GC.KeepAlive(pad.Callback); Marshal.FreeHGlobal(pad.Name); }
        }
    }
}

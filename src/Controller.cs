using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XboxRumble
{
    public enum RumbleCapability { None, DualChannel, Mono }

    public sealed class ControllerDevice
    {
        public readonly int Id;
        public readonly string Name, Family, Connection, StopButtonLabel;
        public readonly RumbleCapability Rumble;
        public readonly ushort Vendor, Product;
        public bool CanRumble { get { return Rumble != RumbleCapability.None; } }
        public string Description
        {
            get { return Family + " · " + Connection + " · " + (Rumble == RumbleCapability.DualChannel ? "双通道震动" : Rumble == RumbleCapability.Mono ? "单通道震动" : "未提供震动支持"); }
        }
        public ControllerDevice(int id, string name, string family, string connection, RumbleCapability rumble, string stopButton, ushort vendor = 0, ushort product = 0)
        { Id = id; Name = name; Family = family; Connection = connection; Rumble = rumble; StopButtonLabel = stopButton; Vendor = vendor; Product = product; }
    }

    public interface IController : IDisposable
    {
        string BackendName { get; }
        string LastError { get; }
        string Notice { get; }
        ControllerDevice[] Enumerate();
        ControllerDevice GetDevice(int id);
        void Update();
        bool Read(int index, out ushort buttons);
        uint Write(int index, ushort left, ushort right);
    }

    public sealed class XInputController : IController
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Gamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct State { public uint Packet; public Gamepad Gamepad; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Vibration { public ushort Left, Right; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetState(uint index, out State state);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SetState(uint index, ref Vibration vibration);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
        private readonly GetState get;
        private readonly SetState set;
        public readonly string ApiName;
        public string BackendName { get { return "XInput / " + ApiName; } }
        public string LastError { get; private set; }
        public string Notice { get; private set; }

        public XInputController(string notice = "")
        {
            Notice = notice;
            foreach (string dll in new[] { "xinput1_4.dll", "xinput9_1_0.dll" })
            {
                IntPtr module = LoadLibrary(Path.Combine(Environment.SystemDirectory, dll));
                if (module == IntPtr.Zero) continue;
                IntPtr getAddress = GetProcAddress(module, "XInputGetState");
                IntPtr setAddress = GetProcAddress(module, "XInputSetState");
                if (getAddress == IntPtr.Zero || setAddress == IntPtr.Zero)
                {
                    FreeLibrary(module);
                    continue;
                }
                get = (GetState)Marshal.GetDelegateForFunctionPointer(getAddress, typeof(GetState));
                set = (SetState)Marshal.GetDelegateForFunctionPointer(setAddress, typeof(SetState));
                ApiName = dll;
                return; // Keep the system DLL loaded for the process lifetime.
            }
            throw new InvalidOperationException("无法加载 Windows XInput。请在 Windows 10 / 11 上运行。");
        }

        public bool Read(int index, out ushort buttons)
        {
            buttons = 0;
            if (index < 0 || index > 3) return false;
            State state;
            uint result = get((uint)index, out state);
            buttons = result == 0 ? state.Gamepad.Buttons : (ushort)0;
            return result == 0;
        }

        public uint Write(int index, ushort left, ushort right)
        {
            if (index < 0 || index > 3) return 1167;
            Vibration vibration = new Vibration { Left = left, Right = right };
            uint error = set((uint)index, ref vibration);
            LastError = error == 0 ? "" : "XInput 错误 " + error;
            return error;
        }
        public ControllerDevice[] Enumerate()
        {
            List<ControllerDevice> devices = new List<ControllerDevice>();
            for (int i = 0; i < 4; i++) { ControllerDevice device = GetDevice(i); if (device != null) devices.Add(device); }
            return devices.ToArray();
        }
        public ControllerDevice GetDevice(int id)
        {
            ushort buttons;
            return Read(id, out buttons) ? new ControllerDevice(id, "Xbox / XInput 手柄 " + (id + 1), "Xbox / XInput", "连接方式未知", RumbleCapability.DualChannel, "B") : null;
        }
        public void Update() { }
        public void Dispose() { }
    }
}

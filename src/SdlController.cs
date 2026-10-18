using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XboxRumble
{
    public static class ControllerFactory
    {
        public static IController Create()
        {
            try { return new SdlController(); }
            catch (Exception error)
            {
                return new XInputController("兼容层不可用，当前仅支持 XInput。" + error.Message);
            }
        }
    }

    public sealed class SdlController : IController
    {
        private sealed class Device
        {
            public IntPtr Gamepad, Joystick, Haptic;
            public ControllerDevice Info;
            public int StopButton = -1;
            public bool Written;
        }
        private readonly object gate = new object();
        private readonly Dictionary<int, Device> opened = new Dictionary<int, Device>();
        private bool disposed, haptics;
        public string BackendName { get; private set; }
        public string LastError { get; private set; }
        public string Notice { get; private set; }
        public SdlController()
        {
            SdlNative.Load();
            SdlNative.SDL_SetMainReady();
            SdlNative.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SdlNative.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
            // Defer PS Bluetooth report switching until the user actually requests rumble.
            SdlNative.SDL_SetHint("SDL_JOYSTICK_ENHANCED_REPORTS", "auto");
            SdlNative.SDL_SetHint("SDL_JOYSTICK_HIDAPI_SWITCH_COMBINE_JOY_CONS", "1");
            if (!SdlNative.SDL_Init(0x2000))
            {
                string error = SdlNative.Error; SdlNative.SDL_Quit();
                throw new InvalidOperationException(error);
            }
            try
            {
                haptics = SdlNative.SDL_Init(0x1000);
                Notice = haptics ? "" : "旧式手柄的力反馈接口未能初始化。";
                SdlNative.SDL_SetGamepadEventsEnabled(false);
                SdlNative.SDL_SetJoystickEventsEnabled(false);
                int version = SdlNative.SDL_GetVersion();
                BackendName = "SDL " + (version / 1000000) + "." + (version / 1000 % 1000) + "." + (version % 1000);
                LastError = "";
            }
            catch { SdlNative.SDL_Quit(); throw; }
        }
        public void Update() { lock (gate) { if (!disposed) SdlNative.SDL_UpdateJoysticks(); } }
        public ControllerDevice[] Enumerate()
        {
            lock (gate)
            {
                if (disposed) return new ControllerDevice[0];
                SdlNative.SDL_UpdateJoysticks();
                int count;
                IntPtr ids = SdlNative.SDL_GetJoysticks(out count);
                if (ids == IntPtr.Zero) { LastError = SdlNative.Error; return new ControllerDevice[0]; }
                HashSet<int> present = new HashSet<int>();
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        int id = Marshal.ReadInt32(ids, i * 4);
                        // Exclude known wheels/flight sticks; unknown USB gamepads can still be listed.
                        int kind = SdlNative.SDL_GetJoystickTypeForID((uint)id);
                        if (kind != 0 && kind != 1) continue;
                        present.Add(id);
                        Device device;
                        if (!opened.TryGetValue(id, out device))
                        {
                            device = Open(id);
                            if (device != null) opened.Add(id, device);
                        }
                    }
                }
                finally { SdlNative.SDL_free(ids); }
                foreach (int id in new List<int>(opened.Keys))
                    if (!present.Contains(id) || !SdlNative.SDL_JoystickConnected(opened[id].Joystick))
                    { Close(opened[id]); opened.Remove(id); }
                List<ControllerDevice> result = new List<ControllerDevice>();
                foreach (Device device in opened.Values) result.Add(device.Info);
                result.Sort(delegate(ControllerDevice a, ControllerDevice b) { return a.Id.CompareTo(b.Id); });
                return result.ToArray();
            }
        }
        private Device Open(int id)
        {
            Device device = new Device();
            if (SdlNative.SDL_IsGamepad((uint)id)) device.Gamepad = SdlNative.SDL_OpenGamepad((uint)id);
            device.Joystick = device.Gamepad != IntPtr.Zero ? SdlNative.SDL_GetGamepadJoystick(device.Gamepad) : SdlNative.SDL_OpenJoystick((uint)id);
            if (device.Joystick == IntPtr.Zero)
            {
                LastError = SdlNative.Error;
                if (device.Gamepad != IntPtr.Zero) SdlNative.SDL_CloseGamepad(device.Gamepad);
                return null;
            }
            try
            {
                RumbleCapability rumble = RumbleCapability.None;
                if (SdlNative.SDL_GetBooleanProperty(SdlNative.SDL_GetJoystickProperties(device.Joystick), "SDL.joystick.cap.rumble", false))
                    rumble = RumbleCapability.DualChannel;
                else if (haptics && SdlNative.SDL_IsJoystickHaptic(device.Joystick))
                {
                    device.Haptic = SdlNative.SDL_OpenHapticFromJoystick(device.Joystick);
                    if (device.Haptic != IntPtr.Zero)
                    {
                        if (SdlNative.SDL_HapticRumbleSupported(device.Haptic) && SdlNative.SDL_InitHapticRumble(device.Haptic)) rumble = RumbleCapability.Mono;
                        else { SdlNative.SDL_CloseHaptic(device.Haptic); device.Haptic = IntPtr.Zero; }
                    }
                }
                int type = device.Gamepad == IntPtr.Zero ? 0 : SdlNative.SDL_GetGamepadType(device.Gamepad);
                string stop = "";
                if (device.Gamepad != IntPtr.Zero)
                {
                    device.StopButton = 1; // East face button, unless a labelled B exists elsewhere.
                    for (int button = 0; button < 4; button++)
                        if (SdlNative.SDL_GetGamepadButtonLabel(device.Gamepad, button) == 2) { device.StopButton = button; break; }
                    int label = SdlNative.SDL_GetGamepadButtonLabel(device.Gamepad, device.StopButton);
                    stop = label == 2 ? "B" : label == 6 ? "○" : "右侧面键";
                }
                int connection = SdlNative.SDL_GetJoystickConnectionState(device.Joystick);
                string name = SdlNative.Text(SdlNative.SDL_GetJoystickName(device.Joystick));
                if (string.IsNullOrWhiteSpace(name)) name = "通用手柄 " + id;
                string family = type == 2 || type == 3 ? "Xbox" : type >= 4 && type <= 6 ? "PlayStation" : type >= 7 && type <= 10 ? "Nintendo Switch" : type == 11 ? "GameCube" : "通用手柄";
                device.Info = new ControllerDevice(id, name, family, connection == 1 ? "有线" : connection == 2 ? "无线" : "连接方式未知", rumble, stop,
                    SdlNative.SDL_GetJoystickVendor(device.Joystick), SdlNative.SDL_GetJoystickProduct(device.Joystick));
                return device;
            }
            catch { Close(device); throw; }
        }
        public ControllerDevice GetDevice(int id)
        {
            lock (gate)
            {
                Device device;
                return !disposed && opened.TryGetValue(id, out device) && SdlNative.SDL_JoystickConnected(device.Joystick) ? device.Info : null;
            }
        }
        public bool Read(int id, out ushort buttons)
        {
            lock (gate)
            {
                buttons = 0;
                Device device;
                if (disposed || !opened.TryGetValue(id, out device) || !SdlNative.SDL_JoystickConnected(device.Joystick)) return false;
                if (device.Gamepad != IntPtr.Zero && device.StopButton >= 0 && SdlNative.SDL_GetGamepadButton(device.Gamepad, device.StopButton)) buttons = 0x2000;
                return true;
            }
        }
        public uint Write(int id, ushort left, ushort right)
        {
            lock (gate)
            {
                Device device;
                if (disposed || !opened.TryGetValue(id, out device) || !SdlNative.SDL_JoystickConnected(device.Joystick)) { LastError = "手柄已断开"; return 1167; }
                if (!device.Info.CanRumble) { LastError = "此设备或驱动未提供震动接口"; return 50; }
                bool success;
                if (device.Info.Rumble == RumbleCapability.Mono)
                    success = left == 0 && right == 0 ? SdlNative.SDL_StopHapticRumble(device.Haptic) : SdlNative.SDL_PlayHapticRumble(device.Haptic, Math.Max(left, right) / 65535f, 750);
                else success = SdlNative.SDL_RumbleJoystick(device.Joystick, left, right, left == 0 && right == 0 ? 0u : 750u);
                device.Written = true;
                LastError = success ? "" : SdlNative.Error;
                return success ? 0u : 31u;
            }
        }
        private void Close(Device device)
        {
            // Never send output merely because a read-only probe opened a device.
            if (device.Written)
            {
                if (device.Haptic != IntPtr.Zero) SdlNative.SDL_StopHapticRumble(device.Haptic);
                else if (device.Joystick != IntPtr.Zero) SdlNative.SDL_RumbleJoystick(device.Joystick, 0, 0, 0);
            }
            if (device.Haptic != IntPtr.Zero) SdlNative.SDL_CloseHaptic(device.Haptic);
            if (device.Gamepad != IntPtr.Zero) SdlNative.SDL_CloseGamepad(device.Gamepad);
            else if (device.Joystick != IntPtr.Zero) SdlNative.SDL_CloseJoystick(device.Joystick);
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (Device device in opened.Values) Close(device);
                opened.Clear(); SdlNative.SDL_Quit();
            }
        }
    }
}

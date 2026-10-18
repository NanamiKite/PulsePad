using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XboxRumble
{
    internal static class SdlNative
    {
        private const string Dll = "SDL3.dll";
        private static IntPtr module;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        internal static void Load()
        {
            if (module != IntPtr.Zero) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", IntPtr.Size == 8 ? "x64" : "x86", Dll);
            if (!File.Exists(path)) throw new FileNotFoundException("缺少兼容库，请保留程序旁的 native 文件夹。", path);
            module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 SDL 手柄兼容库");
        }
        internal static string Text(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            byte[] bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        internal static string Error { get { return Text(SDL_GetError()); } }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetMainReady();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_Init(uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_Quit();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetVersion();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GetError();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetJoystickEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_SetGamepadEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_UpdateJoysticks();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetJoysticks(out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_free(IntPtr pointer);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetJoystickTypeForID(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_IsGamepad(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_OpenGamepad(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_CloseGamepad(IntPtr gamepad);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetGamepadJoystick(IntPtr gamepad);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetGamepadType(IntPtr gamepad);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetGamepadButtonLabel(IntPtr gamepad, int button);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_OpenJoystick(uint id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_CloseJoystick(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_JoystickConnected(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_GetJoystickName(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort SDL_GetJoystickVendor(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort SDL_GetJoystickProduct(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetJoystickConnectionState(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SDL_GetJoystickProperties(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_GetBooleanProperty(uint properties, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.I1)] bool fallback);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_RumbleJoystick(IntPtr joystick, ushort low, ushort high, uint milliseconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_IsJoystickHaptic(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SDL_OpenHapticFromJoystick(IntPtr joystick);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_CloseHaptic(IntPtr haptic);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_HapticRumbleSupported(IntPtr haptic);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_InitHapticRumble(IntPtr haptic);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_PlayHapticRumble(IntPtr haptic, float strength, uint milliseconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SDL_StopHapticRumble(IntPtr haptic);
    }
}

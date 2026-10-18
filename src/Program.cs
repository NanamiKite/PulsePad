using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XboxRumble
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool preview = args.Length == 2 && args[0] == "--preview";
            bool diagnose = args.Length == 2 && args[0] == "--diagnose";
            bool forceXInput = args.Length == 1 && args[0] == "--xinput";
            try
            {
                if (diagnose)
                {
                    using (IController controller = ControllerFactory.Create())
                    {
                        ControllerDevice[] devices = new ControllerDevice[0];
                        for (int i = 0; i < 10; i++) { devices = controller.Enumerate(); Thread.Sleep(50); }
                        StringBuilder report = new StringBuilder();
                        report.AppendLine("Backend: " + controller.BackendName);
                        report.AppendLine("Notice: " + controller.Notice);
                        report.AppendLine("Devices: " + devices.Length);
                        foreach (ControllerDevice device in devices)
                        {
                            report.AppendLine("[" + device.Id + "] " + device.Name);
                            report.AppendLine("  " + device.Description);
                            report.AppendLine("  VID:PID " + device.Vendor.ToString("X4") + ":" + device.Product.ToString("X4"));
                            report.AppendLine("  Stop: " + (string.IsNullOrEmpty(device.StopButtonLabel) ? "keyboard only" : device.StopButtonLabel));
                        }
                        report.AppendLine("Last error: " + controller.LastError);
                        report.AppendLine("Detection only. No vibration command was requested.");
                        File.WriteAllText(args[1], report.ToString(), new UTF8Encoding(true));
                    }
                    return 0;
                }
                bool isFirst;
                using (Mutex mutex = new Mutex(true, "Local\\Xboxtd.RumbleConsole", out isFirst))
                {
                    if (!isFirst && !preview)
                    {
                        MessageBox.Show("程序已在运行，请先关闭旧窗口。", "PulsePad");
                        return 0;
                    }
                    Application app = new Application();
                    using (IController controller = preview || forceXInput ? new XInputController() : ControllerFactory.Create())
                    using (RumbleHost host = new RumbleHost(controller))
                    {
                        DispatcherUnhandledExceptionEventHandler uiError = delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                        {
                            host.Stop("程序异常，播放已停止");
                            if (preview) return;
                            MessageBox.Show(e.Exception.Message, "PulsePad");
                            e.Handled = true;
                        };
                        UnhandledExceptionEventHandler fatalError = delegate { try { host.Stop("程序退出"); } catch { } };
                        SessionSwitchEventHandler sessionChanged = delegate(object sender, SessionSwitchEventArgs e)
                        {
                            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                                host.Stop("Windows 会话已离开，播放已停止");
                        };
                        app.DispatcherUnhandledException += uiError;
                        AppDomain.CurrentDomain.UnhandledException += fatalError;
                        if (!preview) SystemEvents.SessionSwitch += sessionChanged;
                        try
                        {
                            using (MainWindow view = new MainWindow(controller, host, preview))
                            {
                                if (preview) view.RenderPreview(args[1]);
                                else app.Run(view.Window);
                            }
                        }
                        finally
                        {
                            if (!preview) SystemEvents.SessionSwitch -= sessionChanged;
                            AppDomain.CurrentDomain.UnhandledException -= fatalError;
                            app.DispatcherUnhandledException -= uiError;
                        }
                    }
                    if (isFirst) mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length == 2) File.WriteAllText(args[1] + ".error.txt", error.ToString());
                else MessageBox.Show(error.Message, "PulsePad 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}

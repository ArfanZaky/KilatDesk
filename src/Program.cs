using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KilatDesk
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--selftest" || args[0] == "--uitest"))
            {
                AppPaths.Redirect(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KilatDesk-test"));
                HostService.TestLoopbackOnly = true;
                FileTransfer.DownloadDirOverride = AppPaths.File("downloads");
                ViewerSession.ScreenshotDirOverride = AppPaths.File("pictures");
            }
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run(args.Length > 1 ? args[1] : AppPaths.File("selftest.txt"));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Error("UI", e.Exception);
                MessageBox.Show(e.Exception.Message, "KilatDesk - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("fatal", e.ExceptionObject as Exception);

            if (args.Length > 1 && args[0] == "--uitest") { UiTest.Run(args[1]); return 0; }
            if (args.Length > 1 && args[0] == "--inputtest") { UiTest.RunInput(args[1]); return 0; }
            bool autoHost = args.Any(a => a.Equals("--host", StringComparison.OrdinalIgnoreCase));
            string id = "KilatDesk-" + Environment.UserName;
            using (var mutex = new Mutex(true, id, out bool first))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, id + "-show"))
            {
                if (!first) { showSignal.Set(); return 0; } // already running: bring that window up
                var form = new MainForm(autoHost);
                new Thread(() => { while (showSignal.WaitOne()) Ui.Post(form, form.ShowFromTray); }) { IsBackground = true }.Start();
                Log.Info("KilatDesk start" + (autoHost ? " (--host)" : ""));
                Application.Run(form);
            }
            return 0;
        }
    }
}

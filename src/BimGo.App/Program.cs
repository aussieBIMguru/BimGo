using BimGo.Live;
using BimGo.Native;
using BimGo.Scene;
using BimGo.Shell;

// The class belongs to the root namespace
namespace BimGo
{
    /// <summary>
    /// BimGo.exe entry point.
    ///
    /// Usage: <c>BimGo.exe [model.bimgo]</c> or <c>BimGo.exe --session &lt;id&gt;</c> (what Go in Revit runs).
    /// <c>BimGo.exe --kiosk model.bimgo</c> (Round Z-C) opens the file in kiosk mode: visitors walk, fly and look only.
    /// Holding Shift while it starts (or <c>--safe</c>) is a safe start: default settings for that run, nothing saved.
    /// A second launch hands its file / session to the running instance and exits (a kiosk launch only brings the
    /// running window forward).
    /// <c>BimGo.exe --register</c> / <c>--unregister</c> add or remove the .bimgo association and the Start-menu
    /// shortcut for the current user (add <c>--quiet</c> for no message box), then exit.
    /// </summary>
    internal static class Program
    {
        /// <summary>The app version (from the assembly).</summary>
        public static string Version { get; } = GetVersion();

        [STAThread]
        private static int Main(string[] args)
        {
            Utilities.Log_Utils.Initialise("BimGo.App");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Utilities.Log_Utils.Write($"Unhandled: {e.ExceptionObject}");

            // Crisp UI on high-DPI screens
            try { Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* older Windows: stays system-aware */ }

            // --register / --unregister: set up (or remove) the file association, then exit
            if (HasSwitch(args, "--register") || HasSwitch(args, "--unregister")) { return RunRegistration(args); }

            // Round Z-E, safe start: Shift held while BimGo starts → default settings (Basic profile) for this run, nothing saved
            LaunchSettings.SafeStart = (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0 || HasSwitch(args, "--safe");
            if (LaunchSettings.SafeStart) { Utilities.Log_Utils.Write("Safe start: default settings (Basic profile) for this run; nothing is saved."); }

            OpenTarget initial = ParseArguments(args);
            Utilities.Log_Utils.Write($"BimGo {Version} starting{(initial?.Path != null ? ": " + initial.Path : initial?.SessionId != null ? ": session " + initial.SessionId : string.Empty)}{(initial?.Kiosk == true ? " (kiosk)" : string.Empty)}");

            using AppInstance instance = AppInstance.Claim();
            if (!instance.IsFirst)
            {
                AppInstance.ForwardToRunningInstance(initial);
                return 0;
            }

            // Old session folders (Revit crashed / was killed) are tidied once per app start
            LiveSessions.CleanupStale();

            // The installed copy keeps .bimgo files pointing at itself (quick registry check)
            FileAssociation.EnsureRegistered();

            try
            {
                return new AppShell(instance, initial).Run();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"BimGo failed: {ex}");
                string hint = LaunchSettings.SafeStart
                    ? "This was a safe start (default settings), so the cause is probably the graphics driver: update it from the GPU maker's site."
                    : "If BimGo started before, try a safe start: hold Shift while starting BimGo (default settings for that run). Otherwise update the graphics driver.";
                Win32.MessageBoxW(0, $"BimGo could not continue:\n\n{ex.Message}\n\n{hint}\n\nDetails were written to:\n{Utilities.Log_Utils.LogPath}",
                    "BimGo", Win32.MB_OK | Win32.MB_ICONERROR);
                return 1;
            }
        }

        /// <summary>
        /// Handles --register / --unregister (with optional --quiet).
        /// </summary>
        /// <returns>The exit code (0 on success).</returns>
        private static int RunRegistration(string[] args)
        {
            bool quiet = HasSwitch(args, "--quiet");
            bool register = HasSwitch(args, "--register");
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BimGo.exe");

            bool ok = register ? FileAssociation.Register(exe, out string error) : FileAssociation.Unregister(out error);
            string message = ok
                ? register
                    ? $".bimgo files now open in BimGo, and BimGo is in the Start menu.\n\n{exe}"
                    : ".bimgo files are no longer associated with BimGo, and the Start-menu shortcut was removed."
                : $"BimGo could not {(register ? "register" : "unregister")}:\n{error}";
            Utilities.Log_Utils.Write(message.Replace("\n\n", " "));

            if (!quiet)
            {
                Win32.MessageBoxW(0, message, "BimGo", Win32.MB_OK | (ok ? Win32.MB_ICONINFORMATION : Win32.MB_ICONERROR));
            }
            return ok ? 0 : 1;
        }

        private static bool HasSwitch(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// <c>--session &lt;id&gt;</c>, else the first argument that is an existing file (Round Z-C: in kiosk mode with
        /// <c>--kiosk</c>), else null.
        /// </summary>
        private static OpenTarget ParseArguments(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--session", StringComparison.OrdinalIgnoreCase) && LiveProtocol.IsValidSessionId(args[i + 1]))
                {
                    return OpenTarget.Live(args[i + 1]);
                }
            }

            string file = FirstFileArgument(args);
            if (file == null) { return null; }
            return HasSwitch(args, "--kiosk") ? OpenTarget.KioskFile(file) : OpenTarget.File(file);
        }

        /// <summary>
        /// The first argument that is an existing file (switches are skipped, and so are the values of switches that take
        /// one: Round Z-C's <c>--kiosk</c> is a flag, so the file after it is still found).
        /// </summary>
        private static string FirstFileArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (TakesValue(arg)) { i++; } // skip the switch's value
                    continue;
                }
                try
                {
                    string full = Path.GetFullPath(arg.Trim('"'));
                    if (File.Exists(full)) { return full; }
                }
                catch
                {
                    // Not a path
                }
            }
            return null;
        }

        /// <summary>Switches followed by a value (the rest are flags: --kiosk, --safe, --register, --unregister, --quiet).</summary>
        private static bool TakesValue(string name) => string.Equals(name, "--session", StringComparison.OrdinalIgnoreCase);

        private static string GetVersion()
        {
            System.Version version = typeof(Program).Assembly.GetName().Version;
            return version == null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}

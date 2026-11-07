using System;
using System.Globalization;

namespace TaskbarSelect
{
    // This is the protected assembly's sole executable entry point. It has no
    // normal application/profile/Startup/search or uninstall command path.
    internal static class AdministratorPayloadEntry
    {
        internal static bool IsPayloadProcess { get; private set; }
        internal static bool AllowedArguments(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            if (args[0] == "--window-move-worker") return AdministratorLaunch.WorkerArguments(args);
            uint parent; long started;
            return args.Length == 4 && args[0] == "--administrator-helper" && AdministratorLaunch.ValidToken(args[1]) &&
                UInt32.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parent) && parent != 0 &&
                Int64.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out started) && started > 0;
        }
        internal static bool Admit(string[] args, NativeProcessIdentity.Info token, bool protectedPayload)
        { return protectedPayload && AdministratorLaunch.HighToken(token) && AllowedArguments(args); }
        [STAThread]
        internal static int Main(string[] args)
        {
            try {
                if (!Admit(args, NativeProcessIdentity.Current, AdministratorLaunch.IsProtectedPayloadCurrent)) return 2;
                IsPayloadProcess = true;
                if (args[0] == "--administrator-helper" && AdministratorSupport.TryRun(args)) return Environment.ExitCode;
                if (args[0] == "--window-move-worker" && WindowMoveWorker.TryRun(args)) return Environment.ExitCode;
            } catch (Exception) { }
            return 2;
        }
    }
}

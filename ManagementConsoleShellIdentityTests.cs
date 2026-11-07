using System;

namespace TaskbarSelect
{
    // Pure shell-identity receipts and window-lifetime sets; no MMC process,
    // shell query, HWND, elevated operation, profile or input is created.
    internal static class ManagementConsoleShellIdentityTests
    {
        const string Identity = "MMC.Console.Alpha", OtherIdentity = "MMC.Console.Beta";
        internal static void Run(Action<bool, string> check)
        {
            IdentityGrammar(check);
            UniqueIdentity(check);
            ProofProvenance(check);
            ConsoleWindowSet(check);
            ResolverReceipts(check);
            ConsoleTargetShape(check);
        }

        static void IdentityGrammar(Action<bool, string> check)
        {
            check(TaskManagerShellIdentity.ManagementConsoleIdentityAllowed(Identity) &&
                TaskManagerShellIdentity.ManagementConsoleIdentityAllowed(new string('x', 128)),
                "MMC accepts a bounded opaque shell identity without requiring Task Manager's generated-ID pattern");
            check(TaskManagerShellIdentity.ManagementConsoleIdentityAllowed("MMC.\uD83D\uDE00") &&
                !TaskManagerShellIdentity.ManagementConsoleIdentityAllowed("MMC.\uD800") &&
                !TaskManagerShellIdentity.ManagementConsoleIdentityAllowed("MMC.\uDC00") &&
                !TaskManagerShellIdentity.ManagementConsoleIdentityAllowed("MMC.\uD800X") &&
                TaskManagerShellIdentity.HashIdentity("MMC.\uD800") == null &&
                TaskManagerShellIdentity.HashIdentity("MMC.\uD801") == null,
                "Valid Unicode pairs retain identity while malformed UTF-16 cannot collapse into equal replacement-character digests");
            foreach (string malformed in new[] { null, "", " ", Identity + "\n", Identity + "\0", new string('x', 129) })
                check(!TaskManagerShellIdentity.ManagementConsoleIdentityAllowed(malformed),
                    "Missing, control-bearing or overlong MMC identifiers cannot be bound to a console window");
            check(TaskManagerShellIdentity.HashIdentity(Identity).Length == 64 &&
                TaskManagerShellIdentity.HashIdentity(Identity) == TaskManagerShellIdentity.HashIdentity(Identity) &&
                TaskManagerShellIdentity.HashIdentity(Identity) != TaskManagerShellIdentity.HashIdentity(OtherIdentity),
                "Resolver receipts bind an opaque MMC ID through its stable digest rather than disclosing application text");
        }

        static void UniqueIdentity(Action<bool, string> check)
        {
            check(TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity, OtherIdentity }, Identity, 0) &&
                TaskManagerShellIdentity.UniqueShellIdentity(new[] { OtherIdentity, Identity }, Identity, 1),
                "Distinct consoles sharing mmc.exe remain distinguishable by the target window's exact shell identity");
            check(!TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity, Identity }, Identity, 0) &&
                !TaskManagerShellIdentity.UniqueShellIdentity(new[] { OtherIdentity, Identity }, Identity, 0),
                "A duplicate console identity or a different target index cannot authorize moving an arbitrary MMC window");
            check(!TaskManagerShellIdentity.UniqueShellIdentity(null, Identity, 0) &&
                !TaskManagerShellIdentity.UniqueShellIdentity(new string[0], Identity, 0) &&
                !TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity }, Identity, -1) &&
                !TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity }, Identity, 1),
                "Missing or invalid console-window enumeration cannot prove uniqueness");
            check(!TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity, null }, Identity, 0) &&
                !TaskManagerShellIdentity.UniqueShellIdentity(new[] { Identity, "" }, Identity, 0),
                "An unreadable sibling console cannot be silently ignored when proving a unique hosted identity");
        }

        static TaskManagerShellIdentity.Proof Proof(string id, bool shellOnly, uint process, long started)
        {
            return new TaskManagerShellIdentity.Proof(new IntPtr(10), process, started, new IntPtr(20), 30, 40, id, 1000, shellOnly);
        }
        static void ProofProvenance(Action<bool, string> check)
        {
            var console = Proof(Identity, true, 50, 60);
            check(TaskManagerShellIdentity.ProofMatchesTarget(console, new IntPtr(10), 50, 60, new IntPtr(20), Identity),
                "An exact hosted-console shell proof retains the target HWND, PID, process creation and taskbar binding");
            check(!TaskManagerShellIdentity.ProofMatchesTarget(console, new IntPtr(10), 50, 60, new IntPtr(20), OtherIdentity) &&
                !TaskManagerShellIdentity.ProofsMatch(console, Proof(OtherIdentity, true, 50, 60)),
                "The same trusted mmc.exe host cannot substitute another console's shell identity");
            check(!TaskManagerShellIdentity.ProofsMatch(console, Proof(Identity, true, 51, 60)) &&
                !TaskManagerShellIdentity.ProofsMatch(console, Proof(Identity, true, 50, 61)),
                "A new or recycled MMC process invalidates its previous console proof");
            const string generated = "Microsoft.AutoGenerated.{11111111-2222-4333-8444-555555555555}";
            check(!TaskManagerShellIdentity.ProofsMatch(Proof(generated, false, 50, 60), Proof(generated, true, 50, 60)) &&
                !TaskManagerShellIdentity.ProofMatchesTarget(Proof(Identity, false, 50, 60), new IntPtr(10), 50, 60, new IntPtr(20), Identity),
                "A hosted-console proof cannot become a weak Task Manager proof even when identity text coincides");
        }

        static TaskManagerShellIdentity.WindowStamp Stamp(int window, uint process, long started)
        { return new TaskManagerShellIdentity.WindowStamp(new IntPtr(window), process, started); }
        static void ConsoleWindowSet(Action<bool, string> check)
        {
            var first = Stamp(101, 50, 60); var second = Stamp(102, 50, 60);
            var before = new[] { first, second };
            check(TaskManagerShellIdentity.SameWindowSet(before, new[] { second, first }),
                "Harmless enumeration order changes preserve a complete set of exact MMC window lifetimes");
            check(!TaskManagerShellIdentity.SameWindowSet(before, new[] { first }) &&
                !TaskManagerShellIdentity.SameWindowSet(before, new[] { first, Stamp(103, 50, 60) }) &&
                !TaskManagerShellIdentity.SameWindowSet(before, new[] { first, Stamp(102, 50, 61) }),
                "A console close, replacement HWND or process lifetime change during resolution invalidates uniqueness");
            check(!TaskManagerShellIdentity.SameWindowSet(before, new[] { first, first }) &&
                !TaskManagerShellIdentity.SameWindowSet(null, before) &&
                !TaskManagerShellIdentity.SameWindowSet(new TaskManagerShellIdentity.WindowStamp[0], new TaskManagerShellIdentity.WindowStamp[0]),
                "Duplicate, missing or empty console sets cannot act as complete enumeration evidence");
        }

        static string Receipt()
        { return "TSC1|10|50|60|" + TaskManagerShellIdentity.HashIdentity(Identity) + "|1"; }
        static bool Parse(string stdout, string stderr, int exitCode, out string digest)
        { return TaskManagerShellIdentity.ParseManagementConsoleReceipt(stdout, stderr, exitCode, new IntPtr(10), 50, 60, Identity, out digest); }
        static void ResolverReceipts(Action<bool, string> check)
        {
            IntPtr window; uint process; long started;
            var args = new[] { "--read-mmc-identity", "10", "50", "60" };
            check(TaskManagerShellIdentity.ParseManagementConsoleRequest(args, out window, out process, out started) &&
                window == new IntPtr(10) && process == 50 && started == 60 &&
                !TaskManagerShellIdentity.ParseRequest(args, out window, out process, out started),
                "MMC uses a separate bounded resolver mode without broadening Task Manager's request contract");
            string digest;
            check(Parse(Receipt() + "\r\n", "", 0, out digest) && digest == TaskManagerShellIdentity.HashIdentity(Identity) &&
                !Receipt().Contains(Identity),
                "A successful console receipt binds exact lifetime and one unique identity using a digest only");
            foreach (string invalid in new[] {
                Receipt().Replace("TSC1|", "TSM1|"), Receipt().Replace("|10|", "|11|"),
                Receipt().Replace("|50|", "|51|"), Receipt().Replace("|60|", "|61|"),
                Receipt().Substring(0, Receipt().Length - 1) + "2", Receipt() + "extra", Receipt() + "\n\n",
                Receipt().Replace(TaskManagerShellIdentity.HashIdentity(Identity), TaskManagerShellIdentity.HashIdentity(OtherIdentity)),
                Receipt().Replace(TaskManagerShellIdentity.HashIdentity(Identity), Identity)
            }) {
                digest = "stale";
                check(!Parse(invalid, "", 0, out digest) && digest == null,
                    "A stale, ambiguous, malformed or differently bound MMC receipt leaves no usable identity digest");
            }
            check(!Parse(Receipt(), "error", 0, out digest) && !Parse(Receipt(), "", 1, out digest) &&
                !TaskManagerShellIdentity.ParseManagementConsoleReceipt(Receipt(), "", 0, new IntPtr(10), 50, 60, OtherIdentity, out digest),
                "Resolver failure or another console's expected identity cannot authorize the current button");
        }

        static TaskbarTargets.Target ConsoleTarget()
        {
            var proof = Proof(Identity, true, 50, 60);
            return new TaskbarTargets.Target {
                Window = new IntPtr(10), Process = 50, ProcessStarted = 60, Taskbar = new IntPtr(20),
                NeedsNativeSelection = true, ShellOnly = true, ShellProof = proof,
                Button = new TaskbarMover.Click { AppId = Identity, AppName = "An untrusted display name", Taskbar = new IntPtr(20) },
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = new IntPtr(10), Process = 50,
                    ProcessStarted = 60, NeedsNativeSelection = true, ShellOnly = true, ShellProof = proof } }
            };
        }
        static void ConsoleTargetShape(Action<bool, string> check)
        {
            var target = ConsoleTarget();
            check(TaskbarNativeSelection.DirectTargetShape(target),
                "An opaque console identity uses exact bound shell provenance rather than a friendly window or button name");
            target.ShellProof = null;
            check(!TaskbarNativeSelection.DirectTargetShape(target),
                "A plausible console name and opaque app ID alone cannot authorize direct movement");
            target = ConsoleTarget(); target.Windows[0].ShellOnly = false;
            check(!TaskbarNativeSelection.DirectTargetShape(target),
                "A per-window provenance change cannot reuse the console's shell-only proof");
            var fresh = ConsoleTarget(); fresh.ShellOnly = false;
            check(!TaskbarTargets.SameButton(ConsoleTarget(), fresh),
                "A post-release target with changed console provenance is rejected even when its HWND and app ID match");
        }
    }
}

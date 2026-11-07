using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    internal static class GhostAppCatalogTests
    {
        static GhostApp App(string id, string name)
        { return new GhostApp { Id = id, Name = name, AppKey = "exe:C:\\Neutral\\" + id + ".exe" }; }
        static GhostApp Shortcut(string name, string target, string arguments = "", string appId = "")
        { return GhostAppCatalog.FromShortcut(name, "C:\\Neutral\\" + name + ".lnk", target, arguments, "C:\\Neutral", appId, delegate { return true; }); }

        internal static void Run(Action<bool, string> check)
        {
            var apps = new[] { App("terminal", "Terminal"), App("codex", "Codex"), App("paint", "Paint"),
                App("studio", "Visual Studio Code"), App("paint3d", "Paint 3D"), App("nopaint", "NoPaint") };
            check(GhostAppSearch.Find(apps, " paint ", 5).Select(a => a.Id).SequenceEqual(new[] { "paint", "paint3d", "nopaint" }),
                "App search ranks exact, prefix and substring matches predictably");
            check(GhostAppSearch.Find(apps, "CODE", 5).Select(a => a.Id).SequenceEqual(new[] { "codex", "studio" }),
                "App search ignores query casing");
            check(GhostAppSearch.Find(apps, "code visual", 5).Single().Id == "studio", "Every query word must match, in any order");
            check(GhostAppSearch.Find(apps, "Visual-Studio", 5).Single().Id == "studio", "Punctuation separates search words");
            check(GhostAppSearch.Find(apps, "Missing app", 5).Count == 0, "Unmatched query has no guessed or command result");
            check(GhostAppSearch.Find(apps, "cmd /c calc.exe", 5).Count == 0, "Typed commands only search app names");
            check(GhostAppSearch.Find(apps, "", 3).Count == 3, "Empty search returns a bounded app list");
            check(GhostAppSearch.Find(apps, null, 0).Count == 0 && GhostAppSearch.Find(null, "a", 8).Count == 0,
                "Missing catalog and zero result limit are safe");
            check(GhostAppSearch.Find(apps, new string('a', 257), 8).Count == 0, "Pasted text is bounded before searching");
            var duplicates = new List<GhostApp>(apps); duplicates.Add(App("PAINT", "Paint duplicate")); duplicates.Add(null);
            duplicates.Add(new GhostApp { Id = "", Name = "Paint missing identity" });
            check(GhostAppSearch.Find(duplicates, "paint", 20).Count == 3, "Search deduplicates exact launch identity case-insensitively");
            var many = Enumerable.Range(0, 100).Select(i => App("id" + i, "Fixture " + i)).ToList();
            check(GhostAppSearch.Find(many, "", 1000).Count == 50, "Search always limits visible result count");
            var input = apps.Select(a => a.Name).ToArray(); GhostAppSearch.Find(apps, "a", 2);
            check(apps.Select(a => a.Name).SequenceEqual(input), "Query ranking never mutates catalog metadata");
            check(apps[0].ToString() == apps[0].Name && new GhostApp().ToString() == "", "App result accessibility exposes the display name");

            GhostApp direct = Shortcut("Neutral", "C:\\Neutral\\fixture.exe");
            check(direct != null && direct.AppKey == "exe:C:\\Neutral\\fixture.exe" && direct.LaunchPath.EndsWith("Neutral.lnk"),
                "Plain app shortcut exposes an exact executable identity and keeps the original launch link");
            GhostApp grouped = Shortcut("Neutral grouped", "C:\\Neutral\\fixture.exe", "", "Contoso.Neutral");
            check(grouped != null && grouped.AppKey == "app:Contoso.Neutral", "Explicit shortcut app identity is kept exactly");
            GhostApp profile = Shortcut("Neutral profile", "C:\\Neutral\\fixture.exe", "--profile-directory=Default");
            check(profile != null && profile.AppKey == "", "Argument-bearing shortcut cannot claim every window in a shared executable");
            GhostApp launcher = Shortcut("Neutral launcher", "C:\\Neutral\\Update.exe", "--processStart Neutral.exe");
            check(launcher != null && launcher.AppKey == "", "Broker launch shortcut has no guessed running-window authority");
            foreach (string helper in new[] { "Uninstall Neutral", "Remove Neutral", "Help", "Neutral Help", "Readme", "Setup", "Installer" })
                check(Shortcut(helper, "C:\\Neutral\\fixture.exe") == null, "App catalog excludes a helper shortcut");
            foreach (string target in new[] { "C:\\Neutral\\document.pdf", "C:\\Neutral\\folder", "C:\\Neutral\\unins000.exe", "C:\\Neutral\\cmd.exe",
                "C:\\Neutral\\powershell.exe", "C:\\Neutral\\wscript.exe", "C:\\Neutral\\rundll32.exe", "C:\\Neutral\\explorer.exe", "C:\\Neutral\\python.exe" })
                check(Shortcut("Neutral", target) == null, "App catalog excludes documents, uninstallers and generic command hosts");
            foreach (string arguments in new[] { "C:\\Neutral\\document.pdf", "--open C:\\Neutral\\document.pdf", "https://example.invalid/", "/c calc.exe",
                "--uninstall", "--open shell:Desktop", "--script neutral.ps1", "--argument\nother" }) {
                check(Shortcut("Neutral", "C:\\Neutral\\fixture.exe", arguments) == null, "App catalog excludes document/URL/script launch entries");
            }
            foreach (string path in new[] { "fixture.exe", "C:fixture.exe", "\\\\server\\share\\fixture.exe", "shell:AppsFolder", "C:\\Neutral\\fixture.exe\0other" })
                check(Shortcut("Neutral", path) == null, "Catalog rejects relative, network and non-file executable paths");
            check(GhostAppCatalog.FromShortcut("Neutral", "C:\\Neutral\\fixture.lnk", "C:\\Neutral\\fixture.exe", "", "", "", delegate { return false; }) == null,
                "Catalog omits a missing app executable");
            check(Shortcut("Neutral\ncontrol", "C:\\Neutral\\fixture.exe") == null, "Control characters never become visible app names");
            check(GhostAppCatalog.Deduplicate(new[] { direct, direct.Copy(), profile, launcher, null }).Count == 3,
                "Catalog deduplicates only the exact launch target and arguments");
            GhostApp clone = direct.Copy(); clone.Name = "Changed";
            check(direct.Name == "Neutral" && clone.TargetPath == direct.TargetPath, "Launch snapshots copy all validation metadata without sharing mutable entries");
            check(GhostAppCatalog.SameShortcut(direct, direct.Copy()), "Unchanged shortcut metadata is validated");
            foreach (string field in new[] { "Id", "LaunchPath", "TargetPath", "Arguments", "WorkingDirectory", "ShortcutAppId", "AppKey", "PackageId" }) {
                var changed = direct.Copy(); typeof(GhostApp).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(changed, "different");
                check(!GhostAppCatalog.SameShortcut(direct, changed), "Changed shortcut launch/identity metadata invalidates its result");
            }
            check(GhostAppCatalog.ValidPackageId("Contoso.Neutral_123456789abcd!App"), "Packaged identity accepts a canonical AUMID");
            foreach (string invalid in new[] { "", "Contoso.Neutral", "Contoso.Neutral_123!App", "Contoso.Neutral_123456789abcd!", "Contoso.Neutral_123456789abcd!App!Other",
                "Contoso.Neutral_123456789abcd!App /c calc", "shell:AppsFolder\\App", "Contoso.Neutral_123456789abcd!App\n" })
                check(!GhostAppCatalog.ValidPackageId(invalid), "Package activation rejects nonidentity strings");
            check(!GhostAppCatalog.Launch(null).Started, "Launcher rejects an empty app selection before calling Windows");
            check(!GhostAppCatalog.Launch(App("fabricated", "Fabricated")).Started, "Launcher cannot execute a fabricated search result");

            ChromeAttribution(check);
            NewWindowCapabilityAndPlan(check);
            RunOwnedShortcutChecks(check);
        }

        static void NewWindowCapabilityAndPlan(Action<bool, string> check)
        {
            string target = @"C:\Neutral\chrome.exe";
            foreach (string arguments in new[] { "", "  ", "--source-shortcut-location=desktop", "--source-shortcut-location=start-menu",
                "--source-shortcut-location=taskbar", "  --source-shortcut-location=start-menu  " }) {
                var app = Shortcut("Neutral Chrome", target, arguments, "Chrome");
                var before = app.Copy(); GhostAppCatalog.NewWindowLaunchPlan plan;
                check(GhostAppCatalog.CanOpenNewWindow(app), "Only canonical ordinary Chrome metadata exposes new-window capability: " + arguments);
                check(GhostAppCatalog.TryNewWindowPlan(app, app.Copy(), out plan), "An exact reread produces a deterministic Chrome new-window plan: " + arguments);
                string suffix = String.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim();
                check(plan.File == target && plan.File != app.LaunchPath && plan.Verb == "open" && plan.Parameters == "--new-window" + suffix &&
                    plan.Directory == app.WorkingDirectory, "New-window plan uses verified executable, fixed switch, original attribution and working directory");
                check(GhostAppCatalog.SameShortcut(app, before) && app.Arguments == arguments, "New-window planning never changes ordinary shortcut launch metadata");
                app.TargetPath = @"C:\Changed\chrome.exe"; app.Arguments = "--profile-directory=changed"; app.WorkingDirectory = @"C:\Changed";
                check(plan.File == target && plan.Parameters == "--new-window" + suffix && plan.Directory == before.WorkingDirectory,
                    "An admitted plan retains immutable values instead of a mutable catalog reference");
            }
            var canonical = Shortcut("Neutral Chrome", target, "--source-shortcut-location=start-menu", "Chrome");
            check(!GhostAppCatalog.CanOpenNewWindow(null) && !GhostAppCatalog.CanOpenNewWindow(App("fabricated", "Fabricated")),
                "Missing and generic fabricated search entries have no new-window capability");
            foreach (string executable in new[] { @"C:\Neutral\fixture.exe", @"C:\Neutral\chrome_proxy.exe", @"C:\Neutral\chromium.exe", @"C:\Neutral\Update.exe" })
                check(!GhostAppCatalog.CanOpenNewWindow(Shortcut("Neutral", executable, "--source-shortcut-location=start-menu", "Chrome")),
                    "New-window action cannot target brokers, proxies or other executables: " + executable);
            foreach (string id in new[] { "", "chrome", "Chrome.Profile", "Chrome.Other", " Chrome", "Chrome " })
                check(!GhostAppCatalog.CanOpenNewWindow(Shortcut("Neutral Chrome", target, "--source-shortcut-location=start-menu", id)),
                    "New-window action requires the canonical explicit Chrome ID: " + id);
            foreach (string arguments in new[] { "--profile-directory=Default", "--app-id=neutral", "--new-window", "--incognito", "--unknown",
                "--source-shortcut-location=start-menu --profile-directory=Default", "--source-shortcut-location=start-menu --app-id=neutral",
                "--source-shortcut-location=start-menu --new-window", "--source-shortcut-location=unknown", "--source-shortcut-location=start-menu\n" })
                check(!GhostAppCatalog.CanOpenNewWindow(Shortcut("Neutral Chrome", target, arguments, "Chrome")),
                    "No profile, PWA or unapproved argument enters a new-window plan: " + arguments);
            foreach (string field in new[] { "Id", "LaunchPath", "TargetPath", "Arguments", "WorkingDirectory", "ShortcutAppId", "AppKey", "PackageId" }) {
                var reread = canonical.Copy(); typeof(GhostApp).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reread, "changed");
                GhostAppCatalog.NewWindowLaunchPlan plan;
                check(!GhostAppCatalog.TryNewWindowPlan(canonical, reread, out plan) && plan == null,
                    "Changed shortcut metadata refuses before native launch plan creation: " + field);
            }
            foreach (string field in new[] { "Id", "LaunchPath", "TargetPath", "ShortcutAppId", "AppKey", "Name" }) {
                var invalid = canonical.Copy(); typeof(GhostApp).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(invalid, "");
                check(!GhostAppCatalog.CanOpenNewWindow(invalid), "Incomplete catalog authority cannot expose new-window action: " + field);
            }
            var malformed = canonical.Copy(); malformed.TargetPath = "C:\\Neutral\\chrome.exe\0other";
            check(!GhostAppCatalog.CanOpenNewWindow(malformed), "Malformed executable metadata safely refuses capability");
            var notCatalog = canonical.Copy(); notCatalog.CatalogShortcut = false;
            check(!GhostAppCatalog.CanOpenNewWindow(notCatalog), "A noncatalog executable cannot expose new-window action");
            var packaged = canonical.Copy(); packaged.PackageId = "Contoso.Neutral_123456789abcd!App";
            check(!GhostAppCatalog.CanOpenNewWindow(packaged), "Packaged activation is outside the ordinary Chrome new-window action");
            GhostAppCatalog.NewWindowLaunchPlan missing;
            check(!GhostAppCatalog.TryNewWindowPlan(canonical, null, out missing) && missing == null,
                "A missing or unreadable shortcut reread cannot produce a native launch plan");
            check(!GhostAppCatalog.LaunchNewWindow(null).Started && !GhostAppCatalog.LaunchNewWindow(App("fabricated", "Fabricated")).Started,
                "New-window launcher rejects unsupported entries without any real launch or apartment dependency");
        }

        static void ChromeAttribution(Action<bool, string> check)
        {
            const string target = @"C:\Neutral\chrome.exe";
            foreach (string location in new[] { "desktop", "start-menu", "taskbar" }) foreach (bool edgeSpaces in new[] { false, true }) {
                string arguments = "--source-shortcut-location=" + location;
                if (edgeSpaces) arguments = "  " + arguments + "  ";
                var app = Shortcut("Neutral Chrome", target, arguments, "Chrome");
                check(app != null && app.AppKey == "app:Chrome" && app.ShortcutAppId == "Chrome" && app.Arguments == arguments &&
                    app.TargetPath == target && app.CatalogShortcut && app.LaunchPath == @"C:\Neutral\Neutral Chrome.lnk",
                    "Chrome launch attribution retains exact identity and original metadata at " + location + ", edgeSpaces=" + edgeSpaces);
                check(GhostAppCatalog.SameShortcut(app, app.Copy()), "Accepted Chrome metadata remains an exact launch snapshot at " + location + ", edgeSpaces=" + edgeSpaces);
            }
            var casedTarget = Shortcut("Neutral Chrome", @"C:\Neutral\CHROME.EXE", "--source-shortcut-location=start-menu", "Chrome");
            check(casedTarget != null && casedTarget.AppKey == "app:Chrome", "Windows executable basename casing does not erase canonical Chrome identity");
            foreach (string executable in new[] { @"C:\Neutral\fixture.exe", @"C:\Neutral\chromium.exe", @"C:\Neutral\chrome_proxy.exe", @"C:\Neutral\Update.exe" }) {
                var app = Shortcut("Neutral Chrome", executable, "--source-shortcut-location=start-menu", "Chrome");
                check(app == null || app.AppKey == "", "Attribution cannot give another executable Chrome authority: " + executable);
            }
            foreach (string appId in new[] { "", "chrome", "Chromium", "Chrome.Profile", "Chrome.Other", " Chrome", "Chrome " }) {
                var app = Shortcut("Neutral Chrome", target, "--source-shortcut-location=start-menu", appId);
                check(app == null || app.AppKey == "", "Attribution requires the exact canonical shortcut app ID: " + appId);
            }
            foreach (string arguments in new[] {
                "--profile-directory=Default", "--app-id=neutral", "--unknown",
                "--source-shortcut-location=unknown", "--source-shortcut-location=Start-menu", "--SOURCE-SHORTCUT-LOCATION=start-menu",
                "--source-shortcut-location", "--source-shortcut-location start-menu", "--source-shortcut-location=",
                "--source-shortcut-location=start-menu=desktop", "--source-shortcut-location=\"start-menu\"",
                "\"--source-shortcut-location=start-menu\"", "--source-shortcut-location= start-menu",
                "--source-shortcut-location=start-menu --source-shortcut-location=desktop",
                "--source-shortcut-location=start-menu --source-shortcut-location=start-menu",
                "--source-shortcut-location=start-menu --profile-directory=Default",
                "--profile-directory=Default --source-shortcut-location=start-menu",
                "--source-shortcut-location=start-menu --app-id=neutral",
                "--source-shortcut-location=start-menu --unknown", "--source-shortcut-location=start-menu --app=https://example.invalid/",
                "--source-shortcut-location=start-menu\t", "--source-shortcut-location=start-menu\n"
            }) {
                var app = Shortcut("Neutral Chrome", target, arguments, "Chrome");
                check(app == null || app.AppKey == "", "Profile/PWA/unknown/malformed/duplicate Chrome arguments cannot claim placement identity: " + arguments);
            }
        }

        static object Invoke(object value, string name, BindingFlags flags, params object[] arguments)
        { return value.GetType().InvokeMember(name, flags, null, value, arguments); }
        static void WriteShortcut(object shell, string path, string target, string arguments)
        {
            object link = null;
            try {
                link = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path);
                Invoke(link, "TargetPath", BindingFlags.SetProperty, target);
                Invoke(link, "Arguments", BindingFlags.SetProperty, arguments);
                Invoke(link, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(path));
                Invoke(link, "Save", BindingFlags.InvokeMethod);
            } finally { if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link); }
        }
        static void RunOwnedShortcutChecks(Action<bool, string> check)
        {
            // Only this test's temporary shortcut folder is scanned. The target
            // is this owned test executable; no real app or window is launched.
            string root = Path.Combine(Path.GetTempPath(), "TaskbarSelect.GhostCatalog." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); object shell = null; Exception failure = null;
            var worker = new Thread(delegate() {
                try {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    string target;
                    using (var process = Process.GetCurrentProcess()) target = process.MainModule.FileName;
                    WriteShortcut(shell, Path.Combine(root, "Neutral app.lnk"), target, "");
                    WriteShortcut(shell, Path.Combine(root, "Neutral duplicate.lnk"), target, "");
                    WriteShortcut(shell, Path.Combine(root, "Neutral profile.lnk"), target, "--profile=neutral");
                    WriteShortcut(shell, Path.Combine(root, "Neutral document.lnk"), target, "--open C:\\Neutral\\private.pdf");
                    WriteShortcut(shell, Path.Combine(root, "Uninstall Neutral.lnk"), target, "");
                    File.WriteAllText(Path.Combine(root, "Neutral invalid.lnk"), "not a shortcut");
                    var apps = GhostAppCatalog.ReadRoots(new[] { root, root, Path.Combine(root, "missing") }, delegate { return false; });
                    check(apps.Count == 2 && apps.All(a => a.CatalogShortcut), "Read-only COM discovery resolves owned app shortcuts and deduplicates duplicates");
                    check(apps.Any(a => a.Arguments == "--profile=neutral" && a.AppKey == ""), "Resolved shortcut preserves arguments and avoids broker identity guesses");
                    check(apps.All(a => a.WorkingDirectory == root && a.TargetPath.Equals(target, StringComparison.OrdinalIgnoreCase)),
                        "Resolved shortcut preserves working directory and exact executable target");
                    check(GhostAppCatalog.ReadRoots(new[] { root }, delegate { return true; }).Count == 0, "Cancelled discovery returns no stale partial catalog");
                    int requests = 0;
                    check(GhostAppCatalog.ReadRoots(new[] { root }, delegate { return ++requests > 3; }).Count == 0,
                        "Discovery checks cancellation between installed-app entries");
                    check(GhostAppCatalog.ReadRoots(new[] { root }, delegate { throw new InvalidOperationException("Owned cancellation fixture"); }).Count == 0,
                        "A failed cancellation probe stops discovery safely");
                    GhostApp selected = apps.Single(app => app.Arguments == "");
                    WriteShortcut(shell, selected.LaunchPath, target, "--profile=changed");
                    GhostLaunchResult changed = GhostAppCatalog.Launch(selected);
                    check(!changed.Started && !String.IsNullOrWhiteSpace(changed.Error), "A shortcut changed after discovery is rejected before any process can launch");
                } catch (Exception error) { failure = error; }
                finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
            });
            worker.SetApartmentState(ApartmentState.STA); worker.Start(); worker.Join();
            try { if (failure != null) throw new InvalidOperationException("Owned shortcut catalog checks failed.", failure); }
            finally {
                string resolved = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath());
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("TaskbarSelect.GhostCatalog.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to clean an unexpected fixture path.");
                Directory.Delete(resolved, true);
            }
        }
    }
}

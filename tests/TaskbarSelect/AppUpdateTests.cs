using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    // All fixtures are in-memory. These checks never request the network,
    // register desktop hooks, create an installer, or change application state.
    internal static class AppUpdateTests
    {
        sealed class Transport : IAppUpdateTransport
        {
            internal readonly Dictionary<string, byte[]> Responses = new Dictionary<string, byte[]>();
            internal readonly List<string> Calls = new List<string>();
            internal long? AdvertisedLength;
            internal bool UnknownLength;
            internal AppUpdateException Error;
            public AppUpdateResponse Open(Uri address, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); Calls.Add(address.AbsoluteUri);
                if (Error != null) throw Error;
                byte[] bytes;
                if (!Responses.TryGetValue(address.AbsoluteUri, out bytes)) throw new AppUpdateException("Owned transport fixture has no response.");
                return new AppUpdateResponse(new MemoryStream(bytes, false), UnknownLength ? -1 : AdvertisedLength ?? bytes.Length, null);
            }
        }
        sealed class Files : IAppUpdateStaging
        {
            internal MemoryFile Last;
            internal bool FailInspect, Unsafe;
            internal int Creates;
            public IAppUpdateFile Create(string installerName)
            { Creates++; Last = new MemoryFile(installerName) { FailInspect = FailInspect, Safe = !Unsafe }; return Last; }
        }
        sealed class MemoryFile : IAppUpdateFile, IAppUpdateRetainFile
        {
            internal readonly MemoryStream Data = new MemoryStream();
            internal bool Disposed, Retained, Marked, Sealed, MarkerValid = true, FailInspect, Safe = true;
            internal int Inspections;
            readonly string name;
            internal MemoryFile(string fileName) { name = fileName; }
            public string Path { get { return "C:\\Owned-update-fixture\\" + name; } }
            public Stream Content { get { return Data; } }
            public void MarkInternetZone(Uri source, Uri referrer)
            {
                if (!AppUpdatePolicy.InitialUri(source) || !referrer.AbsoluteUri.StartsWith(AppUpdatePolicy.Repository + "/releases/tag/v", StringComparison.Ordinal)) throw new IOException();
                Marked = true;
            }
            public void EnsureSafe() { if (!Safe || (Marked && !MarkerValid)) throw new IOException("Owned unsafe file fixture"); }
            public void Seal() { EnsureSafe(); Sealed = true; }
            public void InspectExecutable(AppUpdateVersion version)
            { Inspections++; if (!Marked || !Sealed || FailInspect) throw new AppUpdateException("Owned invalid executable fixture"); }
            public void RetainAfterLaunch() { Retained = true; }
            public void Dispose() { Disposed = true; }
        }
        sealed class Fixture
        {
            internal readonly Transport Network = new Transport();
            internal readonly Files Staging = new Files();
            internal readonly byte[] Installer = Encoding.ASCII.GetBytes(new string('I', 2048));
            internal readonly string Version;
            internal readonly Dictionary<string, object> Metadata;
            internal readonly AppUpdateService Service;
            internal Fixture(string version)
            {
                Version = version;
                byte[] sums = Encoding.UTF8.GetBytes(AppUpdateService.Hash(Installer) + "  " + AppUpdatePolicy.InstallerName(version) + "\n");
                Metadata = Release(version, Installer, sums);
                Network.Responses[AppUpdatePolicy.AssetUri(version, AppUpdatePolicy.InstallerName(version)).AbsoluteUri] = Installer;
                Network.Responses[AppUpdatePolicy.AssetUri(version, "SHA256SUMS.txt").AbsoluteUri] = sums;
                Service = new AppUpdateService(Network, Staging); SetFeed(Metadata);
            }
            internal void SetFeed(params object[] releases)
            { Network.Responses[AppUpdatePolicy.Feed] = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(releases)); }
            internal AppUpdateRelease Available()
            { return Service.Check("1.0.0-beta.13", CancellationToken.None).Release; }
        }
        static Dictionary<string, object> Asset(string version, string name, byte[] bytes)
        {
            return new Dictionary<string, object> { { "name", name }, { "size", bytes.Length }, { "state", "uploaded" },
                { "browser_download_url", AppUpdatePolicy.AssetUri(version, name).AbsoluteUri }, { "digest", "sha256:" + AppUpdateService.Hash(bytes) } };
        }
        static Dictionary<string, object> Release(string version, byte[] installer, byte[] sums)
        {
            return new Dictionary<string, object> { { "tag_name", "v" + version }, { "draft", false }, { "prerelease", version.Contains("-beta.") },
                { "html_url", AppUpdatePolicy.Repository + "/releases/tag/v" + version }, { "body", "Owned release notes\nNothing is launched." },
                { "assets", new object[] { Asset(version, AppUpdatePolicy.InstallerName(version), installer), Asset(version, "SHA256SUMS.txt", sums) } } };
        }
        static Dictionary<string, object> InstallerAsset(Fixture fixture)
        { return (Dictionary<string, object>)((object[])fixture.Metadata["assets"])[0]; }
        static bool Fails(Action action)
        { try { action(); return false; } catch (AppUpdateException) { return true; } }
        static bool Cancels(Action action)
        { try { action(); return false; } catch (OperationCanceledException) { return true; } }
        static AppUpdateVersion Version(string value)
        { AppUpdateVersion parsed; if (!AppUpdateVersion.TryParse(value, out parsed)) throw new Exception("Owned invalid version fixture"); return parsed; }

        internal static void Run(Action<bool, string> check)
        {
            foreach (string value in new[] { "1.0.0-beta.13", "1.0.0-beta.14", "1.0.0", "2.0.1", "65535.65535.65535-beta.65535" }) {
                AppUpdateVersion version;
                check(AppUpdateVersion.TryParse(value, out version) && version.Text == value, "Update version accepts only supported canonical release " + value);
            }
            foreach (string value in new[] { "v1.0.0", "1.0", "01.0.0", "1.0.0-beta.0", "1.0.0-beta.01", "1.0.0-rc.1", "1.0.0-beta.14+dev", "1.0.0-beta.14/evil", "1.0.0\r\n", "65536.0.0", "1.0.0-beta.999999999999999999999", "", null }) {
                AppUpdateVersion version;
                check(!AppUpdateVersion.TryParse(value, out version), "Update version rejects noncanonical/unsupported metadata " + (value ?? "null"));
            }
            check(Version("1.0.0-beta.14").CompareTo(Version("1.0.0-beta.13")) > 0 && Version("1.0.0-beta.10").CompareTo(Version("1.0.0-beta.9")) > 0, "Update beta ordering is numeric");
            check(Version("1.0.0").CompareTo(Version("1.0.0-beta.65535")) > 0 && Version("1.0.1-beta.1").CompareTo(Version("1.0.0")) > 0, "Update stable and future beta ordering follows release precedence");

            check(AppUpdatePolicy.Repository == "https://github.com/mrlevivel/TaskbarSelect" &&
                AppUpdatePolicy.Feed == "https://api.github.com/repos/mrlevivel/TaskbarSelect/releases?per_page=100" &&
                AppUpdateForm.OfficialReleases == AppUpdatePolicy.Repository + "/releases",
                "Update and browser links identify the clean public repository");
            check(AppUpdatePolicy.InitialUri(new Uri(AppUpdatePolicy.Feed)), "Update feed is exact official releases endpoint including prereleases");
            check(AppUpdatePolicy.InitialUri(AppUpdatePolicy.AssetUri("1.0.0-beta.14", AppUpdatePolicy.InstallerName("1.0.0-beta.14"))), "Update exact official Setup asset accepted");
            check(AppUpdatePolicy.RedirectUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned?signature=value")), "Update CDN allows only fixed repository asset namespace");
            foreach (string address in new[] {
                "https://api.github.com/repos/mrlevivel/taskbar-select/releases?per_page=100",
                "https://github.com/mrlevivel/taskbar-select/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "http://github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "https://github.com.evil.test/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "https://github.com/other/project/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "https://name@github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "https://github.com:444/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/SHA256SUMS.txt",
                "https://github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/SHA256SUMS.txt?download=elsewhere",
                "https://api.github.com/repos/mrlevivel/TaskbarSelect/releases/latest",
                "https://github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.14/other.exe" })
                check(!AppUpdatePolicy.InitialUri(new Uri(address)), "Update request blocks unapproved source " + address);
            foreach (string address in new[] {
                "https://release-assets.githubusercontent.com/github-production-release-asset/1378387389/owned",
                "http://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned",
                "https://release-assets.githubusercontent.com.evil.test/github-production-release-asset/1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/999/owned",
                "https://objects.githubusercontent.com/github-production-release-asset/1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/%2eowned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/%2e%2e/1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/./owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/../1449520856/owned",
                "https://example.test/installer.exe" })
                check(!AppUpdatePolicy.RedirectUri(new Uri(address)), "Update redirect blocks unapproved CDN address " + address);
            check(AppUpdatePolicy.RedirectUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned?signature=escaped%2Fvalue")),
                "Update original-path validation retains legitimate escaped signed query values");
            foreach (string location in new[] {
                "https://release-assets.githubusercontent.com/github-production-release-asset/1378387389/owned?signature=value",
                "/github-production-release-asset/1449520856/owned",
                "//release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/%2eowned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/./owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/../1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned\r\nHeader:bad",
                " https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned#fragment",
                "https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned?" + new string('x', 8192)
            }) {
                Uri redirect;
                check(!AppUpdatePolicy.TryRedirectLocation(location, out redirect), "Update actual transport rejects unsafe raw Location before URI normalization");
            }
            Uri legitimateRedirect;
            check(AppUpdatePolicy.TryRedirectLocation("https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/owned?signature=escaped%2Fvalue", out legitimateRedirect),
                "Update actual transport accepts bounded official absolute CDN redirect with signed query");

            var fixture = new Fixture("1.0.0-beta.14");
            var result = fixture.Service.Check("1.0.0-beta.13", CancellationToken.None);
            check(result.Status == AppUpdateCheckStatus.Available && result.Release.Version == "1.0.0-beta.14" && fixture.Network.Calls.Count == 1, "Update discovers newer beta through one explicit unauthenticated list check");
            check(result.Release.ReleasePage.AbsoluteUri == AppUpdatePolicy.Repository + "/releases/tag/v1.0.0-beta.14", "Update release link is fixed official tag");
            check(fixture.Service.Check("1.0.0-beta.14", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate, "Update never offers same beta");
            check(fixture.Service.Check("1.0.0-beta.15", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate, "Update never downgrades newer installed beta");
            check(fixture.Service.Check("1.0.0", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate, "Update never replaces stable with same-core beta");
            check(fixture.Service.Check("invalid", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update unknown local version fails closed");
            fixture.SetFeed();
            check(fixture.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate, "Update empty public feed is handled");
            fixture.SetFeed(Enumerable.Repeat((object)fixture.Metadata, 100).ToArray());
            check(fixture.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update bounded full feed cannot falsely claim complete version knowledge");
            fixture.Network.Responses[AppUpdatePolicy.Feed] = Encoding.UTF8.GetBytes("{not valid JSON");
            check(fixture.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update malformed JSON fails without leaking parser details");
            fixture.Network.Responses[AppUpdatePolicy.Feed] = Encoding.UTF8.GetBytes("{}");
            check(fixture.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update object response cannot masquerade as release list");
            fixture.Network.Responses[AppUpdatePolicy.Feed] = new byte[AppUpdatePolicy.MaxFeedBytes + 1]; fixture.Network.UnknownLength = true;
            check(fixture.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update streamed feed respects byte bound even without Content-Length");

            foreach (Action<Fixture> change in new Action<Fixture>[] {
                f => f.Metadata["html_url"] = "https://github.com/mrlevivel/taskbar-select/releases/tag/v1.0.0-beta.14",
                f => InstallerAsset(f)["browser_download_url"] = "https://github.com/mrlevivel/taskbar-select/releases/download/v1.0.0-beta.14/TaskbarSelect-1.0.0-beta.14-Setup.exe",
                f => f.Metadata["html_url"] = "https://example.test/release",
                f => InstallerAsset(f)["browser_download_url"] = "https://example.test/payload.exe",
                f => InstallerAsset(f)["browser_download_url"] = AppUpdatePolicy.AssetUri("1.0.0-beta.13", AppUpdatePolicy.InstallerName("1.0.0-beta.13")).AbsoluteUri,
                f => InstallerAsset(f)["size"] = AppUpdatePolicy.MaxInstallerBytes + 1,
                f => InstallerAsset(f)["size"] = -1,
                f => InstallerAsset(f)["size"] = "2048",
                f => InstallerAsset(f)["state"] = "new",
                f => InstallerAsset(f)["digest"] = null,
                f => InstallerAsset(f)["digest"] = "sha1:" + new string('a', 40),
                f => f.Metadata["assets"] = new object[] { InstallerAsset(f), InstallerAsset(f), ((object[])f.Metadata["assets"])[1] },
                f => f.Metadata["assets"] = new object[] { InstallerAsset(f) }
            }) {
                var invalid = new Fixture("1.0.0-beta.14"); change(invalid); invalid.SetFeed(invalid.Metadata);
                check(invalid.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable && invalid.Staging.Creates == 0,
                    "Update invalid selected metadata cannot download or stage an installer");
            }
            foreach (Action<Fixture> change in new Action<Fixture>[] {
                f => f.Metadata["draft"] = true,
                f => f.Metadata.Remove("draft"),
                f => f.Metadata["prerelease"] = false,
                f => f.Metadata["tag_name"] = "v1.0.0-rc.1"
            }) {
                var ignored = new Fixture("1.0.0-beta.14"); change(ignored); ignored.SetFeed(ignored.Metadata);
                check(ignored.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate, "Update ignores draft, unknown-channel and inconsistent prerelease records");
            }
            var ordered = new Fixture("1.0.0-beta.15");
            ordered.SetFeed(new Fixture("1.0.0-beta.14").Metadata, ordered.Metadata, new Fixture("1.0.0-beta.9").Metadata);
            check(ordered.Available().Version == "1.0.0-beta.15", "Update release selection uses numeric version rather than list creation order");
            var stable = new Fixture("1.0.0"); stable.SetFeed(stable.Metadata, new Fixture("1.0.0-beta.99").Metadata);
            check(stable.Available().Version == "1.0.0", "Update stable release outranks its own prereleases");
            var stableChannel = new Fixture("1.0.1-beta.1");
            check(stableChannel.Service.Check("1.0.0", CancellationToken.None).Status == AppUpdateCheckStatus.UpToDate,
                "Update actual stable check never opts an installed stable release into future beta channel");
            stableChannel.SetFeed(stableChannel.Metadata, new Fixture("1.0.1").Metadata);
            check(stableChannel.Service.Check("1.0.0", CancellationToken.None).Release.Version == "1.0.1",
                "Update stable check selects newer stable release while ignoring beta candidates");

            var download = new Fixture("1.0.0-beta.14"); AppUpdateRelease available = download.Available();
            var progress = new List<AppUpdateProgress>();
            using (VerifiedAppUpdate verified = download.Service.Download(available, progress.Add, CancellationToken.None)) {
                check(verified.Version == available.Version && verified.Sha256 == AppUpdateService.Hash(download.Installer), "Update returns verified digest-bound descriptor only after full download");
                check(download.Staging.Last.Marked && download.Staging.Last.Sealed && !download.Staging.Last.Disposed && download.Staging.Last.Inspections == 1, "Update descriptor retains sealed file lease and Internet zone marker");
                check(progress.Count > 0 && progress.Last().BytesReceived == download.Installer.Length && progress.Last().TotalBytes == download.Installer.Length, "Update progress counts bounded transferred installer bytes");
                verified.VerifyForLaunch();
                check(download.Staging.Last.Inspections == 2, "Update launch verification repeats executable checks under live lease");
                download.Staging.Last.Data.Position = 0; download.Staging.Last.Data.WriteByte(0x00);
                check(Fails(verified.VerifyForLaunch), "Update launch rehash rejects changed staged bytes");
            }
            check(download.Staging.Last.Disposed && !download.Staging.Last.Retained, "Update cancellation/unused descriptor disposes owned staged file");
            var launched = new Fixture("1.0.0-beta.14");
            using (VerifiedAppUpdate verified = launched.Service.Download(launched.Available(), null, CancellationToken.None)) verified.MarkLaunched();
            check(launched.Staging.Last.Disposed && launched.Staging.Last.Retained, "Update launched Setup retains backing file when lease is disposed");
            var zone = new Fixture("1.0.0-beta.14");
            using (VerifiedAppUpdate verified = zone.Service.Download(zone.Available(), null, CancellationToken.None)) {
                zone.Staging.Last.MarkerValid = false;
                bool rejected = false; try { verified.VerifyForLaunch(); } catch (IOException) { rejected = true; }
                check(rejected, "Update launch fails when Internet zone marker is removed or changed");
            }
            foreach (Action<Fixture> change in new Action<Fixture>[] {
                f => f.Network.Responses[AppUpdatePolicy.AssetUri(f.Version, AppUpdatePolicy.InstallerName(f.Version)).AbsoluteUri] = new byte[2048],
                f => f.Network.Responses[AppUpdatePolicy.AssetUri(f.Version, AppUpdatePolicy.InstallerName(f.Version)).AbsoluteUri] = new byte[2049],
                f => f.Network.Responses[AppUpdatePolicy.AssetUri(f.Version, AppUpdatePolicy.InstallerName(f.Version)).AbsoluteUri] = new byte[2047],
                f => f.Staging.FailInspect = true,
                f => f.Staging.Unsafe = true
            }) {
                var rejected = new Fixture("1.0.0-beta.14"); AppUpdateRelease release = rejected.Available(); change(rejected); rejected.Network.UnknownLength = true;
                check(Fails(() => rejected.Service.Download(release, null, CancellationToken.None)) && rejected.Staging.Last.Disposed,
                    "Update truncated, oversized, corrupted, wrong executable or unsafe staging is rejected and cleaned");
            }
            var badSums = new Fixture("1.0.0-beta.14"); AppUpdateRelease sumsRelease = badSums.Available();
            badSums.Network.Responses[AppUpdatePolicy.AssetUri(badSums.Version, "SHA256SUMS.txt").AbsoluteUri][0] ^= 1;
            check(Fails(() => badSums.Service.Download(sumsRelease, null, CancellationToken.None)) && badSums.Staging.Creates == 0, "Update checksum asset digest failure prevents installer staging");
            var disagree = new Fixture("1.0.0-beta.14"); InstallerAsset(disagree)["digest"] = "sha256:" + new string('0', 64); disagree.SetFeed(disagree.Metadata);
            check(Fails(() => disagree.Service.Download(disagree.Available(), null, CancellationToken.None)) && disagree.Staging.Creates == 0, "Update independent GitHub digest and checksum disagreement fails closed");
            var reportedSize = new Fixture("1.0.0-beta.14"); AppUpdateRelease sizedRelease = reportedSize.Available(); reportedSize.Network.AdvertisedLength = 1;
            check(Fails(() => reportedSize.Service.Download(sizedRelease, null, CancellationToken.None)) && reportedSize.Staging.Creates == 0, "Update wrong server Content-Length is rejected before allocation/staging");
            check(Fails(() => new Fixture("1.0.0-beta.14").Service.Download(null, null, CancellationToken.None)), "Update null or absent metadata is never installable");
            var noDigests = new Fixture("1.0.0-beta.14");
            check(Fails(() => noDigests.Service.Download(new AppUpdateRelease("1.0.0-beta.14", "", 2048, 110, null, null), null, CancellationToken.None)) && noDigests.Network.Calls.Count == 0,
                "Update download independently rejects missing required GitHub digests before any request");

            string hash = new string('a', 64), name = AppUpdatePolicy.InstallerName("1.0.0-beta.14");
            check(AppUpdateService.Checksum(hash + "  " + name + "\r\n", name) == hash && AppUpdateService.Checksum(hash.ToUpperInvariant() + " *" + name + "\n", name) == hash, "Update checksum parser accepts standard text/binary SHA256SUMS forms");
            foreach (string sums in new[] { hash + "  ../" + name, hash + "  " + name + "\n" + hash + "  " + name, hash + " " + name, "bad", hash + "  " + name + "\0" })
                check(Fails(() => AppUpdateService.Checksum(sums, name)), "Update checksum parser rejects traversal substitute, duplicate, malformed or NUL record");
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel(); var cancelled = new Fixture("1.0.0-beta.14");
                check(Cancels(() => cancelled.Service.Check("1.0.0-beta.13", cancellation.Token)) && cancelled.Network.Calls.Count == 0, "Update cancelled check performs no request");
                AppUpdateRelease release = cancelled.Available();
                check(Cancels(() => cancelled.Service.Download(release, null, cancellation.Token)) && cancelled.Staging.Creates == 0, "Update cancelled download creates no staging file");
            }
            using (var cancellation = new CancellationTokenSource()) {
                var cancelled = new Fixture("1.0.0-beta.14"); AppUpdateRelease release = cancelled.Available();
                check(Cancels(() => cancelled.Service.Download(release, p => cancellation.Cancel(), cancellation.Token)) && cancelled.Staging.Last.Disposed,
                    "Update cancellation during transferred bytes cleans file and never returns installer");
            }
            var unavailable = new Fixture("1.0.0-beta.14") { };
            unavailable.Network.Error = new AppUpdateException("Public updates are not available yet.");
            check(unavailable.Service.Check("1.0.0-beta.13", CancellationToken.None).Status == AppUpdateCheckStatus.Unavailable, "Update private/unreachable repository is a friendly unavailable result");

            byte[] pe = new byte[1024]; pe[0] = 0x4d; pe[1] = 0x5a; pe[0x3c] = 0x80;
            pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x84] = 0x4c; pe[0x85] = 1;
            pe[0x94] = 0xe0; pe[0x96] = 2; pe[0x98] = 0x0b; pe[0x99] = 1; pe[0xdc] = 2;
            check(AppUpdateStaging.PortableExecutable(new MemoryStream(pe, false)), "Update PE inspection accepts bounded Windows executable header fixture");
            foreach (Action<byte[]> corrupt in new Action<byte[]>[] {
                p => p[0] = 0,
                p => p[0x3f] = 0x7f,
                p => p[0x80] = 0,
                p => p[0x84] = 0,
                p => p[0x97] = 0x20,
                p => p[0x98] = 0,
                p => p[0xdc] = 3
            }) {
                byte[] changed = (byte[])pe.Clone(); corrupt(changed);
                check(!AppUpdateStaging.PortableExecutable(new MemoryStream(changed, false)), "Update actual executable inspection rejects corrupt/non-GUI/DLL/non-PE header");
            }
            check(!AppUpdateStaging.PortableExecutable(new MemoryStream(new byte[100], false)), "Update actual executable inspection rejects truncated header");
            var installerVersion = Version("1.0.0-beta.14");
            check(AppUpdateStaging.InstallerMetadata(installerVersion, "Taskbar Select", "1.0.0-beta.14", 1, 0, 0, 14),
                "Update installer identity accepts exact product and semantic/numeric version");
            check(AppUpdateStaging.InstallerMetadata(installerVersion, "Taskbar Select".PadRight(60), "1.0.0-beta.14".PadRight(50), 1, 0, 0, 14),
                "Update installer identity accepts only documented Inno ASCII trailing padding");
            foreach (string invalidName in new[] { " Taskbar Select", "Taskbar Select\t", "Taskbar Select\r\n", "Taskbar Select\0", "Taskbar Select\u00a0", "Other product", null })
                check(!AppUpdateStaging.InstallerMetadata(installerVersion, invalidName, "1.0.0-beta.14", 1, 0, 0, 14),
                    "Update installer product rejects leading/non-ASCII whitespace, controls and alternate identity");
            foreach (string invalidVersion in new[] { " 1.0.0-beta.14", "1.0.0-beta.14\t", "1.0.0-beta.14\r", "1.0.0-beta.14\0", "1.0.0-beta.14\u00a0", "1.0.0-beta.13", "1.0.0.14", null })
                check(!AppUpdateStaging.InstallerMetadata(installerVersion, "Taskbar Select", invalidVersion, 1, 0, 0, 14),
                    "Update installer text version rejects controls, mismatch or numeric fallback despite correct numeric fields");
            check(!AppUpdateStaging.InstallerMetadata(installerVersion, "Taskbar Select".PadRight(60), "1.0.0-beta.14".PadRight(50), 1, 0, 0, 13),
                "Update correct padded text cannot conceal wrong installer numeric version");
        }
    }
}

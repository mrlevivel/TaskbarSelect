using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace TaskbarSelect
{
    internal sealed class AppUpdateException : Exception
    {
        internal AppUpdateException(string message) : base(message) { }
    }

    internal enum AppUpdateCheckStatus { Available, UpToDate, Unavailable }

    internal sealed class AppUpdateCheckResult
    {
        internal AppUpdateCheckStatus Status { get; private set; }
        internal AppUpdateRelease Release { get; private set; }
        internal string Message { get; private set; }
        internal AppUpdateCheckResult(AppUpdateCheckStatus status, AppUpdateRelease release, string message)
        { Status = status; Release = release; Message = message; }
    }

    internal sealed class AppUpdateProgress
    {
        internal long BytesReceived { get; private set; }
        internal long TotalBytes { get; private set; }
        internal AppUpdateProgress(long received, long total) { BytesReceived = received; TotalBytes = total; }
    }

    internal sealed class AppUpdateRelease
    {
        internal string Version { get; private set; }
        internal string Notes { get; private set; }
        internal Uri ReleasePage { get; private set; }
        internal long InstallerSize { get; private set; }
        internal Uri InstallerUrl { get; private set; }
        internal Uri ChecksumsUrl { get; private set; }
        internal long ChecksumsSize { get; private set; }
        internal string InstallerDigest { get; private set; }
        internal string ChecksumsDigest { get; private set; }
        internal AppUpdateRelease(string version, string notes, long size, long sumsSize, string installerDigest, string sumsDigest)
        {
            Version = version; Notes = notes; InstallerSize = size; ChecksumsSize = sumsSize;
            ReleasePage = new Uri(AppUpdatePolicy.Repository + "/releases/tag/v" + version);
            InstallerUrl = AppUpdatePolicy.AssetUri(version, AppUpdatePolicy.InstallerName(version));
            ChecksumsUrl = AppUpdatePolicy.AssetUri(version, "SHA256SUMS.txt");
            InstallerDigest = installerDigest; ChecksumsDigest = sumsDigest;
        }
    }

    // This deliberately accepts only this project's released channels. It does
    // not turn arbitrary tag text into a path, command, or version downgrade.
    internal sealed class AppUpdateVersion : IComparable<AppUpdateVersion>
    {
        internal readonly int Major, Minor, Patch, Beta;
        internal readonly string Text;
        AppUpdateVersion(string text, int major, int minor, int patch, int beta)
        { Text = text; Major = major; Minor = minor; Patch = patch; Beta = beta; }
        internal static bool TryParse(string text, out AppUpdateVersion version)
        {
            version = null;
            if (text == null || text.Length > 60) return false;
            Match match = Regex.Match(text, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.([1-9][0-9]*))?\z", RegexOptions.CultureInvariant);
            int major, minor, patch, beta = 0;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out major) ||
                !Int32.TryParse(match.Groups[2].Value, out minor) || !Int32.TryParse(match.Groups[3].Value, out patch) ||
                (match.Groups[4].Success && !Int32.TryParse(match.Groups[4].Value, out beta)) ||
                major > 65535 || minor > 65535 || patch > 65535 || beta > 65535) return false;
            version = new AppUpdateVersion(text, major, minor, patch, beta); return true;
        }
        public int CompareTo(AppUpdateVersion other)
        {
            if (other == null) return 1;
            int result = Major.CompareTo(other.Major);
            if (result == 0) result = Minor.CompareTo(other.Minor);
            if (result == 0) result = Patch.CompareTo(other.Patch);
            if (result != 0 || Beta == other.Beta) return result;
            if (Beta == 0) return 1;
            if (other.Beta == 0) return -1;
            return Beta.CompareTo(other.Beta);
        }
    }

    internal static class AppUpdatePolicy
    {
        internal const string Repository = "https://github.com/mrlevivel/TaskbarSelect";
        internal const string Feed = "https://api.github.com/repos/mrlevivel/TaskbarSelect/releases?per_page=100";
        internal const int MaxFeedBytes = 2 * 1024 * 1024, MaxChecksumsBytes = 64 * 1024;
        internal const long MaxInstallerBytes = 64 * 1024 * 1024;
        internal static string InstallerName(string version) { return "TaskbarSelect-" + version + "-Setup.exe"; }
        internal static Uri AssetUri(string version, string file)
        { return new Uri(Repository + "/releases/download/v" + version + "/" + file); }
        internal static bool SameUri(Uri actual, Uri expected)
        { return actual != null && expected != null && String.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal); }
        internal static bool Https(Uri uri)
        { return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment); }
        internal static bool InitialUri(Uri uri)
        {
            if (!Https(uri)) return false;
            if (SameUri(uri, new Uri(Feed))) return true;
            if (uri.Host != "github.com" || !String.IsNullOrEmpty(uri.Query)) return false;
            string prefix = "/mrlevivel/TaskbarSelect/releases/download/v";
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] pieces = uri.AbsolutePath.Substring(prefix.Length).Split('/');
            AppUpdateVersion version;
            return pieces.Length == 2 && AppUpdateVersion.TryParse(pieces[0], out version) &&
                (pieces[1] == InstallerName(version.Text) || pieces[1] == "SHA256SUMS.txt");
        }
        internal static bool RedirectUri(Uri uri)
        {
            // No wildcard GitHub domain, arbitrary API URLs, HTTP downgrade,
            // redirects back to a different repo, or user-supplied mirror.
            return Https(uri) && RawRedirectPath(uri.OriginalString) && uri.Host == "release-assets.githubusercontent.com" &&
                uri.AbsolutePath.StartsWith("/github-production-release-asset/1449520856/", StringComparison.Ordinal) &&
                uri.AbsolutePath.IndexOf('%') < 0 && uri.AbsolutePath.IndexOf("..", StringComparison.Ordinal) < 0;
        }
        static bool RawRedirectPath(string original)
        {
            // Uri canonicalizes escaped dots before AbsolutePath is returned.
            // Inspect the server's original absolute Location as well; signed
            // query values may contain escapes, whereas this CDN path may not.
            int authority = original.IndexOf("://", StringComparison.Ordinal);
            if (authority < 0) return false;
            int start = original.IndexOf('/', authority + 3);
            if (start < 0) return false;
            int end = original.IndexOfAny(new[] { '?', '#' }, start);
            string path = end < 0 ? original.Substring(start) : original.Substring(start, end - start);
            return path.IndexOf('%') < 0 && path.IndexOf('\\') < 0 && path.IndexOf("..", StringComparison.Ordinal) < 0 && path.IndexOf("/./", StringComparison.Ordinal) < 0;
        }
        internal static bool TryRedirectLocation(string location, out Uri address)
        {
            address = null;
            if (String.IsNullOrEmpty(location) || location.Length > 8192 ||
                !location.StartsWith("https://release-assets.githubusercontent.com/github-production-release-asset/1449520856/", StringComparison.Ordinal)) return false;
            foreach (char character in location) if (character < 0x21 || character > 0x7e) return false;
            return RawRedirectPath(location) && Uri.TryCreate(location, UriKind.Absolute, out address) && RedirectUri(address);
        }
        internal static string Digest(object value)
        {
            string digest = value as string;
            if (digest == null || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant))
                throw new AppUpdateException("The release has an unsupported integrity digest. Open its GitHub release page instead.");
            return digest.Substring(7).ToLowerInvariant();
        }
    }

    internal sealed class AppUpdateResponse : IDisposable
    {
        internal readonly Stream Content;
        internal readonly long Length;
        readonly IDisposable owner;
        internal AppUpdateResponse(Stream content, long length, IDisposable lifetime)
        { Content = content; Length = length; owner = lifetime; }
        public void Dispose() { try { Content.Dispose(); } finally { if (owner != null) owner.Dispose(); } }
    }
    internal interface IAppUpdateTransport
    { AppUpdateResponse Open(Uri address, CancellationToken cancellation); }
    internal interface IAppUpdateFile : IDisposable
    {
        string Path { get; }
        Stream Content { get; }
        void MarkInternetZone(Uri source, Uri referrer);
        void Seal();
        void EnsureSafe();
        void InspectExecutable(AppUpdateVersion version);
    }
    internal interface IAppUpdateStaging
    { IAppUpdateFile Create(string installerName); }

    internal sealed class VerifiedAppUpdate : IDisposable
    {
        IAppUpdateFile file;
        readonly AppUpdateVersion version;
        bool launched;
        internal string InstallerPath { get { if (file == null) throw new ObjectDisposedException("VerifiedAppUpdate"); return file.Path; } }
        internal string Version { get { return version.Text; } }
        internal string Sha256 { get; private set; }
        internal VerifiedAppUpdate(IAppUpdateFile staged, AppUpdateVersion release, string digest)
        { file = staged; version = release; Sha256 = digest; }
        internal void VerifyForLaunch()
        {
            if (file == null) throw new ObjectDisposedException("VerifiedAppUpdate");
            file.EnsureSafe();
            if (AppUpdateService.Hash(file.Content, CancellationToken.None) != Sha256)
                throw new AppUpdateException("The downloaded installer changed. Check for updates and download it again.");
            file.InspectExecutable(version); file.EnsureSafe();
        }
        internal void MarkLaunched() { launched = true; }
        public void Dispose()
        {
            if (file != null) {
                // A running Setup may outlive this app. Release its lease, but
                // do not remove the backing download after ownership transfer.
                var retained = file as IAppUpdateRetainFile;
                if (launched && retained != null) retained.RetainAfterLaunch();
                file.Dispose(); file = null;
            }
        }
    }
    internal interface IAppUpdateRetainFile { void RetainAfterLaunch(); }

    internal sealed class AppUpdateService
    {
        readonly IAppUpdateTransport transport;
        readonly IAppUpdateStaging staging;
        internal AppUpdateService(IAppUpdateTransport network, IAppUpdateStaging files)
        { if (network == null || files == null) throw new ArgumentNullException(); transport = network; staging = files; }
        internal static AppUpdateService CreateDefault()
        { return new AppUpdateService(new AppUpdateHttpTransport(), new AppUpdateStaging()); }
        internal AppUpdateCheckResult Check(string currentVersion, CancellationToken cancellation)
        {
            AppUpdateVersion current;
            if (!AppUpdateVersion.TryParse(currentVersion, out current))
                return new AppUpdateCheckResult(AppUpdateCheckStatus.Unavailable, null, "This build does not have a supported release version. Open GitHub to check manually.");
            try {
                byte[] bytes = ReadBounded(new Uri(AppUpdatePolicy.Feed), AppUpdatePolicy.MaxFeedBytes, -1, cancellation);
                AppUpdateRelease release = ParseReleaseList(Encoding.UTF8.GetString(bytes), current);
                return release == null ? new AppUpdateCheckResult(AppUpdateCheckStatus.UpToDate, null, "You are using the latest available version.") :
                    new AppUpdateCheckResult(AppUpdateCheckStatus.Available, release, "Version " + release.Version + " is available.");
            } catch (OperationCanceledException) { throw; }
            catch (AppUpdateException error) { return new AppUpdateCheckResult(AppUpdateCheckStatus.Unavailable, null, error.Message); }
            catch (Exception error) {
                cancellation.ThrowIfCancellationRequested();
                if (error is OutOfMemoryException || error is StackOverflowException || error is ThreadAbortException) throw;
                return new AppUpdateCheckResult(AppUpdateCheckStatus.Unavailable, null, "Updates could not be checked. Try again later or open the GitHub releases page.");
            }
        }
        internal VerifiedAppUpdate Download(AppUpdateRelease release, Action<AppUpdateProgress> progress, CancellationToken cancellation)
        {
            AppUpdateVersion version;
            if (release == null || !AppUpdateVersion.TryParse(release.Version, out version) || release.InstallerSize < 1024 ||
                release.InstallerSize > AppUpdatePolicy.MaxInstallerBytes || release.ChecksumsSize < 1 || release.ChecksumsSize > AppUpdatePolicy.MaxChecksumsBytes ||
                !ValidDigest(release.InstallerDigest) || !ValidDigest(release.ChecksumsDigest) ||
                !AppUpdatePolicy.SameUri(release.InstallerUrl, AppUpdatePolicy.AssetUri(release.Version, AppUpdatePolicy.InstallerName(release.Version))) ||
                !AppUpdatePolicy.SameUri(release.ChecksumsUrl, AppUpdatePolicy.AssetUri(release.Version, "SHA256SUMS.txt")))
                throw new AppUpdateException("The update metadata is invalid. Open GitHub to update manually.");
            cancellation.ThrowIfCancellationRequested();
            byte[] sums = ReadBounded(release.ChecksumsUrl, AppUpdatePolicy.MaxChecksumsBytes, release.ChecksumsSize, cancellation);
            if (Hash(sums) != release.ChecksumsDigest)
                throw new AppUpdateException("The release checksum file failed its integrity check. Nothing was installed.");
            string expected = Checksum(Encoding.UTF8.GetString(sums), AppUpdatePolicy.InstallerName(version.Text));
            if (release.InstallerDigest != expected)
                throw new AppUpdateException("The release integrity values disagree. Nothing was installed.");
            IAppUpdateFile file = null;
            try {
                file = staging.Create(AppUpdatePolicy.InstallerName(version.Text)); file.EnsureSafe();
                file.MarkInternetZone(release.InstallerUrl, release.ReleasePage); file.EnsureSafe();
                using (AppUpdateResponse response = transport.Open(release.InstallerUrl, cancellation)) {
                    if (response.Length >= 0 && response.Length != release.InstallerSize) throw SizeError();
                    Copy(response.Content, file.Content, release.InstallerSize, release.InstallerSize, progress, cancellation);
                }
                file.Content.Flush(); file.EnsureSafe(); file.Seal(); file.EnsureSafe();
                string actual = Hash(file.Content, cancellation);
                if (actual != expected) throw new AppUpdateException("The installer failed its SHA-256 check. Nothing was installed; please retry later.");
                file.InspectExecutable(version); file.EnsureSafe(); cancellation.ThrowIfCancellationRequested();
                var result = new VerifiedAppUpdate(file, version, actual); file = null; return result;
            } catch (OperationCanceledException) { throw; }
            catch (AppUpdateException) { throw; }
            catch (Exception error) {
                cancellation.ThrowIfCancellationRequested();
                if (error is OutOfMemoryException || error is StackOverflowException || error is ThreadAbortException) throw;
                throw new AppUpdateException("The update could not be downloaded safely. Check your connection and available disk space, then try again.");
            } finally { if (file != null) file.Dispose(); }
        }
        internal static AppUpdateRelease ParseReleaseList(string json, AppUpdateVersion current)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = AppUpdatePolicy.MaxFeedBytes, RecursionLimit = 32 };
            object root;
            try { root = serializer.DeserializeObject(json); }
            catch (ArgumentException) { throw new AppUpdateException("GitHub returned an unreadable update list. Try again later."); }
            object[] releases = root as object[];
            if (releases == null || releases.Length > 100) throw new AppUpdateException("GitHub returned an unexpected update list.");
            if (releases.Length == 100) throw new AppUpdateException("The release list exceeds this updater's safe check limit. Open GitHub to check for the newest release.");
            Dictionary<string, object> chosen = null;
            AppUpdateVersion newest = current;
            foreach (object item in releases) {
                var release = item as Dictionary<string, object>;
                if (release == null || BooleanValue(release, "draft") != false) continue;
                string tag = StringValue(release, "tag_name"); AppUpdateVersion version;
                if (tag == null || !tag.StartsWith("v", StringComparison.Ordinal) || !AppUpdateVersion.TryParse(tag.Substring(1), out version)) continue;
                if (BooleanValue(release, "prerelease") != (version.Beta != 0)) continue;
                if (current.Beta == 0 && version.Beta != 0) continue;
                if (version.CompareTo(newest) > 0) { newest = version; chosen = release; }
            }
            if (chosen == null) return null;
            Uri page;
            if (!Uri.TryCreate(StringValue(chosen, "html_url"), UriKind.Absolute, out page) ||
                !AppUpdatePolicy.SameUri(page, new Uri(AppUpdatePolicy.Repository + "/releases/tag/v" + newest.Text)))
                throw new AppUpdateException("The update release does not point to the official project.");
            object assetsValue; object[] assets;
            if (!chosen.TryGetValue("assets", out assetsValue) || (assets = assetsValue as object[]) == null || assets.Length > 100)
                throw new AppUpdateException("This release has no supported installer. Open its GitHub release page instead.");
            Dictionary<string, object> installer = null, checksums = null;
            string fileName = AppUpdatePolicy.InstallerName(newest.Text);
            foreach (object item in assets) {
                var asset = item as Dictionary<string, object>; if (asset == null) continue;
                string name = StringValue(asset, "name");
                if (name == fileName) { if (installer != null) throw new AppUpdateException("The release contains ambiguous installer assets."); installer = asset; }
                if (name == "SHA256SUMS.txt") { if (checksums != null) throw new AppUpdateException("The release contains ambiguous checksum assets."); checksums = asset; }
            }
            ValidateAsset(installer, newest.Text, fileName, 1024, AppUpdatePolicy.MaxInstallerBytes);
            ValidateAsset(checksums, newest.Text, "SHA256SUMS.txt", 1, AppUpdatePolicy.MaxChecksumsBytes);
            string notes = StringValue(chosen, "body") ?? "";
            if (notes.Length > 16000) notes = notes.Substring(0, 16000) + "\r\nSee GitHub for the full release notes.";
            return new AppUpdateRelease(newest.Text, notes, IntegerValue(installer, "size"), IntegerValue(checksums, "size"),
                AppUpdatePolicy.Digest(Value(installer, "digest")), AppUpdatePolicy.Digest(Value(checksums, "digest")));
        }
        static void ValidateAsset(Dictionary<string, object> asset, string version, string name, long minimum, long maximum)
        {
            Uri address;
            if (asset == null || StringValue(asset, "state") != "uploaded" || IntegerValue(asset, "size") < minimum || IntegerValue(asset, "size") > maximum ||
                !Uri.TryCreate(StringValue(asset, "browser_download_url"), UriKind.Absolute, out address) || !AppUpdatePolicy.SameUri(address, AppUpdatePolicy.AssetUri(version, name)))
                throw new AppUpdateException("The release installer or checksums are incomplete or invalid. Open GitHub to check this release.");
        }
        static object Value(Dictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) ? item : null; }
        static string StringValue(Dictionary<string, object> value, string key) { return Value(value, key) as string; }
        static bool? BooleanValue(Dictionary<string, object> value, string key) { object item = Value(value, key); return item is bool ? (bool?)item : null; }
        static long IntegerValue(Dictionary<string, object> value, string key)
        { object item = Value(value, key); return item is int ? (int)item : item is long ? (long)item : -1; }
        byte[] ReadBounded(Uri address, int maximum, long expected, CancellationToken cancellation)
        {
            using (var response = transport.Open(address, cancellation)) using (var data = new MemoryStream()) {
                if (response.Length > maximum || (response.Length >= 0 && expected >= 0 && response.Length != expected)) throw SizeError();
                Copy(response.Content, data, maximum, expected, null, cancellation); return data.ToArray();
            }
        }
        static void Copy(Stream source, Stream destination, long maximum, long expected, Action<AppUpdateProgress> progress, CancellationToken cancellation)
        {
            var watch = Stopwatch.StartNew(); byte[] buffer = new byte[32768]; long received = 0; int count;
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromMinutes(3)) throw new AppUpdateException("The update download timed out. Please try again.");
                count = source.Read(buffer, 0, buffer.Length); if (count == 0) break;
                received += count; if (received > maximum) throw SizeError();
                destination.Write(buffer, 0, count);
                if (progress != null) progress(new AppUpdateProgress(received, expected));
            }
            cancellation.ThrowIfCancellationRequested();
            if (expected >= 0 && received != expected) throw SizeError();
        }
        static AppUpdateException SizeError() { return new AppUpdateException("The update download has an unexpected size. Nothing was installed."); }
        static bool ValidDigest(string value)
        { return value != null && Regex.IsMatch(value, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant); }
        internal static string Checksum(string text, string name)
        {
            string found = null;
            if (text.IndexOf('\0') >= 0) throw new AppUpdateException("The checksum file is invalid.");
            foreach (string line in text.TrimStart('\uFEFF').Split('\n')) {
                string value = line.TrimEnd('\r'); if (String.IsNullOrWhiteSpace(value)) continue;
                Match match = Regex.Match(value, @"\A([a-fA-F0-9]{64}) [ *]([^\r\n]+)\z", RegexOptions.CultureInvariant);
                if (!match.Success) throw new AppUpdateException("The checksum file is invalid.");
                if (match.Groups[2].Value != name) continue;
                if (found != null) throw new AppUpdateException("The installer checksum is ambiguous.");
                found = match.Groups[1].Value.ToLowerInvariant();
            }
            if (found == null) throw new AppUpdateException("The release has no checksum for this installer.");
            return found;
        }
        internal static string Hash(byte[] data) { using (var stream = new MemoryStream(data, false)) return Hash(stream, CancellationToken.None); }
        internal static string Hash(Stream stream, CancellationToken cancellation)
        {
            stream.Position = 0;
            using (var hash = SHA256.Create()) {
                byte[] buffer = new byte[32768]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0); stream.Position = 0;
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    internal sealed class AppUpdateHttpTransport : IAppUpdateTransport
    {
        sealed class Lifetime : IDisposable
        {
            readonly HttpWebResponse response; readonly CancellationTokenRegistration cancellation;
            internal Lifetime(HttpWebResponse value, CancellationTokenRegistration registration) { response = value; cancellation = registration; }
            public void Dispose() { cancellation.Dispose(); response.Close(); }
        }
        public AppUpdateResponse Open(Uri address, CancellationToken cancellation)
        {
            if (!AppUpdatePolicy.InitialUri(address)) throw new AppUpdateException("The update address is not an official Taskbar Select address.");
            bool feed = AppUpdatePolicy.SameUri(address, new Uri(AppUpdatePolicy.Feed));
            // Framework 4 applications otherwise inherit obsolete TLS defaults.
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            for (int redirects = 0; redirects <= 3; redirects++) {
                cancellation.ThrowIfCancellationRequested();
                var request = (HttpWebRequest)WebRequest.Create(address);
                request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = 20000; request.ReadWriteTimeout = 15000;
                request.UserAgent = "TaskbarSelect-Updater"; request.Accept = feed ? "application/vnd.github+json" : "application/octet-stream";
                if (feed) request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                request.UseDefaultCredentials = false; request.Credentials = null; request.PreAuthenticate = false; request.KeepAlive = false;
                var registration = cancellation.Register(request.Abort); HttpWebResponse response = null;
                try {
                    response = (HttpWebResponse)request.GetResponse(); int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308) {
                        Uri redirect;
                        if (feed || redirects == 3 || !AppUpdatePolicy.TryRedirectLocation(response.Headers["Location"], out redirect))
                            throw new AppUpdateException("GitHub redirected the update to an unexpected address. Nothing was installed.");
                        address = redirect; response.Close(); registration.Dispose(); continue;
                    }
                    if (status != 200) throw new AppUpdateException("GitHub could not provide this update. Please try again later.");
                    return new AppUpdateResponse(response.GetResponseStream(), response.ContentLength, new Lifetime(response, registration));
                } catch (WebException error) {
                    registration.Dispose(); if (response != null) response.Close(); cancellation.ThrowIfCancellationRequested();
                    var failure = error.Response as HttpWebResponse;
                    int status = failure == null ? 0 : (int)failure.StatusCode; if (failure != null) failure.Close();
                    if (status == 403 || status == 429) throw new AppUpdateException("GitHub is limiting update requests or this repository is not publicly accessible. Try again later or open GitHub.");
                    if (status == 404) throw new AppUpdateException("Public updates are not available yet, or this release was removed. Open GitHub to check access.");
                    throw new AppUpdateException("Could not connect securely to GitHub. Check your connection and try again.");
                } catch { registration.Dispose(); if (response != null) response.Close(); throw; }
            }
            throw new AppUpdateException("Too many update redirects.");
        }
    }

    internal sealed class AppUpdateStaging : IAppUpdateStaging
    {
        public IAppUpdateFile Create(string installerName) { return new StagedFile(installerName); }
        sealed class StagedFile : IAppUpdateFile, IAppUpdateRetainFile
        {
            readonly List<SafeFileHandle> directories = new List<SafeFileHandle>();
            FileStream stream, zoneLease; string directory, path, internetZone; bool retain, ownsFile, ownsDirectory;
            public string Path { get { return path; } }
            public Stream Content { get { return stream; } }
            internal StagedFile(string name)
            {
                if (System.IO.Path.GetFileName(name) != name || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    throw new AppUpdateException("The installer name is invalid.");
                try {
                    string local = System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                    if (local.Length < 3 || local[1] != ':' || local.StartsWith("\\", StringComparison.Ordinal)) throw new IOException();
                    string parent = System.IO.Path.Combine(local, "TaskbarSelect", "Updates");
                    LockAncestors(local);
                    foreach (string next in new[] { System.IO.Path.Combine(local, "TaskbarSelect"), parent }) {
                        Directory.CreateDirectory(next); LockDirectory(next);
                    }
                    directory = System.IO.Path.Combine(parent, "download-" + Guid.NewGuid().ToString("N"));
                    var security = new DirectorySecurity();
                    SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                    security.SetAccessRuleProtection(true, false); security.SetOwner(user);
                    foreach (SecurityIdentifier identity in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    // Atomic creation distinguishes our new directory from an
                    // existing path. Do not adopt/delete a pre-existing folder.
                    if (!CreateFreshDirectory(directory, IntPtr.Zero)) throw new IOException("The update directory already exists or cannot be created.");
                    ownsDirectory = true; Directory.SetAccessControl(directory, security); LockDirectory(directory);
                    path = System.IO.Path.Combine(directory, name);
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 32768, FileOptions.None);
                    ownsFile = true;
                    EnsureSafe();
                } catch { Dispose(); throw new AppUpdateException("A secure local update folder could not be created. Open GitHub to download the installer manually."); }
            }
            void LockAncestors(string pathToLock)
            {
                string root = System.IO.Path.GetPathRoot(pathToLock); LockDirectory(root);
                string current = root;
                foreach (string part in pathToLock.Substring(root.Length).Split(System.IO.Path.DirectorySeparatorChar)) {
                    if (part.Length == 0) continue; current = System.IO.Path.Combine(current, part); LockDirectory(current);
                }
            }
            void LockDirectory(string value)
            {
                SafeFileHandle handle = OpenPath(value, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                FileInformation information;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out information) || (information.Attributes & 0x10) == 0 || (information.Attributes & 0x400) != 0) {
                    handle.Dispose(); throw new IOException("Unsafe update directory.");
                }
                directories.Add(handle);
            }
            public void EnsureSafe()
            {
                FileInformation information;
                if (stream == null || stream.SafeFileHandle.IsInvalid || !GetFileInformationByHandle(stream.SafeFileHandle, out information) ||
                    (information.Attributes & (0x400 | 0x10)) != 0 || information.Links != 1) throw new IOException("Unsafe update file.");
                var final = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
                if (length == 0 || length >= final.Capacity || !String.Equals(final.ToString(), "\\\\?\\" + path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The update file does not match its staging path.");
                foreach (SafeFileHandle handle in directories)
                    if (handle.IsInvalid || !GetFileInformationByHandle(handle, out information) || (information.Attributes & 0x400) != 0)
                        throw new IOException("The update directory changed.");
                if (internetZone != null) {
                    if (zoneLease == null || !zoneLease.CanRead || zoneLease.Length > 2048) throw new IOException("The Internet zone marker changed.");
                    zoneLease.Position = 0; byte[] data = new byte[checked((int)zoneLease.Length)]; int received = 0, count;
                    while (received < data.Length && (count = zoneLease.Read(data, received, data.Length - received)) > 0) received += count;
                    if (received != data.Length || Encoding.UTF8.GetString(data) != internetZone) throw new IOException("The Internet zone marker changed.");
                    zoneLease.Position = 0;
                }
            }
            public void MarkInternetZone(Uri source, Uri referrer)
            {
                if (internetZone != null || !AppUpdatePolicy.InitialUri(source) || referrer == null ||
                    !referrer.AbsoluteUri.StartsWith(AppUpdatePolicy.Repository + "/releases/tag/v", StringComparison.Ordinal)) throw new IOException("Unsafe Internet zone metadata.");
                internetZone = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=" + referrer.AbsoluteUri + "\r\nHostUrl=" + source.AbsoluteUri + "\r\n";
                using (var zone = InternetZone(path, true)) {
                    byte[] data = new UTF8Encoding(false).GetBytes(internetZone); zone.Write(data, 0, data.Length); zone.Flush();
                }
                zoneLease = InternetZone(path, false);
                EnsureSafe();
            }
            public void Seal()
            {
                EnsureSafe(); FileInformation before, after;
                if (!stream.CanWrite || !GetFileInformationByHandle(stream.SafeFileHandle, out before)) throw new IOException("The update writer is unavailable.");
                stream.Flush(true); stream.Dispose(); stream = null;
                // Pinned ancestors and our private directory remain locked. The
                // new handle excludes any pre-existing or new writer, and its
                // physical file identity must still be the one we created.
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out after) || before.VolumeSerial != after.VolumeSerial ||
                    before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow) throw new IOException("The update file changed while sealing.");
                EnsureSafe();
            }
            public void RetainAfterLaunch() { retain = true; }
            public void InspectExecutable(AppUpdateVersion version)
            {
                EnsureSafe();
                if (!PortableExecutable(stream)) throw new AppUpdateException("The download is not a supported Windows installer. Nothing was installed.");
                FileVersionInfo metadata = FileVersionInfo.GetVersionInfo(path);
                if (!InstallerMetadata(version, metadata.ProductName, metadata.ProductVersion, metadata.FileMajorPart, metadata.FileMinorPart,
                    metadata.FileBuildPart, metadata.FilePrivatePart))
                    throw new AppUpdateException("The installer identity or version does not match this release. Nothing was installed.");
            }
            public void Dispose()
            {
                if (zoneLease != null) { zoneLease.Dispose(); zoneLease = null; }
                if (stream != null) { stream.Dispose(); stream = null; }
                // Never recursively delete, follow links, or enumerate user files.
                try { if (!retain && ownsFile && path != null && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose(); directories.Clear();
                try { if (!retain && ownsDirectory && directory != null && Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        internal static bool PortableExecutable(Stream stream)
        {
            try {
                if (stream.Length < 1024) return false; stream.Position = 0;
                var reader = new BinaryReader(stream); if (reader.ReadUInt16() != 0x5a4d) return false;
                stream.Position = 0x3c; int offset = reader.ReadInt32(); if (offset < 64 || offset > stream.Length - 256) return false;
                stream.Position = offset; if (reader.ReadUInt32() != 0x00004550) return false;
                ushort machine = reader.ReadUInt16(); if (machine != 0x14c && machine != 0x8664) return false;
                stream.Position = offset + 20; ushort optionalSize = reader.ReadUInt16(), flags = reader.ReadUInt16();
                if (optionalSize < 70 || (flags & 2) == 0 || (flags & 0x2000) != 0) return false;
                ushort magic = reader.ReadUInt16(); if (magic != 0x10b && magic != 0x20b) return false;
                stream.Position = offset + 24 + 68; return reader.ReadUInt16() == 2;
            } catch (EndOfStreamException) { return false; }
            finally { stream.Position = 0; }
        }
        internal static bool InstallerMetadata(AppUpdateVersion version, string productName, string productVersion, int major, int minor, int patch, int revision)
        {
            // Inno keeps fixed-size version resource slots and pads them with
            // ASCII spaces. Accept that padding only, never leading whitespace,
            // controls, alternate identities or a numeric-version fallback.
            return version != null && productName != null && productVersion != null &&
                productName.TrimEnd(' ') == "Taskbar Select" && productVersion.TrimEnd(' ') == version.Text &&
                major == version.Major && minor == version.Minor && patch == version.Patch && revision == version.Beta;
        }
        static FileStream InternetZone(string file, bool create)
        {
            // Framework FileStream(string) rejects ADS paths before reaching
            // NTFS. Native opening uses only this fixed suffix on our already
            // pinned, owned base file; no caller can supply another stream.
            SafeFileHandle handle = OpenPath(file + ":Zone.Identifier", create ? 0x40000000u : 0x80000000u,
                1, IntPtr.Zero, create ? 1u : 3u, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The Internet zone marker could not be opened safely."); }
            try { return new FileStream(handle, create ? FileAccess.Write : FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle OpenPath(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateFreshDirectory(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    }
}

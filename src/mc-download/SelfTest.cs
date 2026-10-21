using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace McDownload
{
    internal static class SelfTest
    {
        public static int Run(IProgress<string> log)
        {
            var check = new Check(log);
            var root = Path.Combine(Path.GetTempPath(), "mc-dl-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var savedPlatform = Platform.Current;
            var savedSettings = SettingsStore.OverridePath;
            try
            {
                Rules(check);
                Paths(check);
                Versions(check);
                Libraries(check, root);
                Assets(check, root);
                Files(check, root);
                Settings(check, root);
                LegacyAndProfile(check, root);
                Http(check, root);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    FormChecks(check);
                else if (log != null)
                    log.Report("跳过界面自测：当前不是 Windows");
                return check.Passed;
            }
            finally
            {
                Platform.Current = savedPlatform;
                SettingsStore.OverridePath = savedSettings;
                try { Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }

        static void Rules(Check check)
        {
            check.That(McDownloader.RulesAllow(null, "x86_64"), "null rules allow");
            check.That(!McDownloader.RulesAllow(JsonUtil.Parse("[]"), "x86_64"), "empty rules deny");
            check.That(!McDownloader.RulesAllow(JsonUtil.Parse("[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"windows\"}}]"), "x86_64"), "later disallow wins");
            check.That(McDownloader.RulesAllow(JsonUtil.Parse("[{\"action\":\"disallow\",\"os\":{\"name\":\"windows\"}},{\"action\":\"allow\"}]"), "x86_64"), "later allow wins");
            check.That(!McDownloader.RulesAllow(JsonUtil.Parse("[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}]"), "x86_64"), "features do not match");
            var archRule = JsonUtil.Parse("[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"windows\",\"arch\":\"x86\"}}]");
            check.That(McDownloader.RulesAllow(archRule, "x86_64"), "x86 disallow ignored on x64");
            check.That(!McDownloader.RulesAllow(archRule, "x86"), "x86 disallow matches");
            check.That(!McDownloader.RulesAllow(JsonUtil.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"osx\"}}]"), "x86_64"), "osx only");
            check.That(McDownloader.RulesAllow(JsonUtil.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"windows\"}}]"), "x86_64"), "windows allow");
        }

        static void Paths(Check check)
        {
            check.That(McDownloader.MirrorUrl(McDownloader.ManifestUrl) == "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", "manifest mirror");
            check.That(McDownloader.MirrorUrl("https://piston-data.mojang.com/v1/objects/aa/client.jar") == "https://bmclapi2.bangbang93.com/v1/objects/aa/client.jar", "piston-data mirror");
            check.That(McDownloader.MirrorUrl("https://launchermeta.mojang.com/mc/game/version_manifest.json") == "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json", "launchermeta mirror");
            check.That(McDownloader.MirrorUrl("https://launcher.mojang.com/v1/objects/xx/client.jar") == "https://bmclapi2.bangbang93.com/v1/objects/xx/client.jar", "launcher mirror");
            check.That(McDownloader.MirrorUrl("http://libraries.minecraft.net/com/foo.jar") == "https://bmclapi2.bangbang93.com/maven/com/foo.jar", "libraries mirror");
            check.That(McDownloader.MirrorUrl("https://resources.download.minecraft.net/ab/cdef") == "https://bmclapi2.bangbang93.com/assets/ab/cdef", "assets mirror");

            var officialFirst = McDownloader.SourceUrls("https://libraries.minecraft.net/a.jar", false);
            check.That(officialFirst.Length == 2 && officialFirst[0] == "https://libraries.minecraft.net/a.jar" && officialFirst[1].StartsWith("https://bmclapi2.bangbang93.com/maven/", StringComparison.Ordinal), "official first");
            var mirrorFirst = McDownloader.SourceUrls("https://libraries.minecraft.net/a.jar", true);
            check.That(mirrorFirst.Length == 2 && mirrorFirst[0] == "https://bmclapi2.bangbang93.com/maven/a.jar" && mirrorFirst[1] == "https://libraries.minecraft.net/a.jar", "mirror first");
            var plain = McDownloader.SourceUrls("https://example.com/a.jar", true);
            check.That(plain.Length == 1 && plain[0] == "https://example.com/a.jar", "unknown host stays single");
            var upgraded = McDownloader.SourceUrls("http://libraries.minecraft.net/a.jar", false);
            check.That(upgraded[0] == "https://libraries.minecraft.net/a.jar", "http upgraded");

            check.That(McDownloader.MavenPath("com.mojang:brigadier:1.0.17", null) == "com/mojang/brigadier/1.0.17/brigadier-1.0.17.jar", "maven plain");
            check.That(McDownloader.MavenPath("org.lwjgl:lwjgl:3.3.1:natives-windows", null) == "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar", "maven classifier in name");
            check.That(McDownloader.MavenPath("net.foo:bar:1.0:natives-windows@zip", null) == "net/foo/bar/1.0/bar-1.0-natives-windows.zip", "maven extension");
            check.That(McDownloader.MavenPath("net.foo:bar:1.0", "natives-windows-64") == "net/foo/bar/1.0/bar-1.0-natives-windows-64.jar", "maven explicit classifier");
            check.That(McDownloader.UserAgent.IndexOf("2.0.0", StringComparison.Ordinal) >= 0, "user agent version");
        }

        static void Versions(Check check)
        {
            var manifest = JsonUtil.ParseObject("{\"latest\":{\"release\":\"1.20.1\",\"snapshot\":\"24w14a\"},\"versions\":[{\"id\":\"24w14a\",\"type\":\"snapshot\"},{\"id\":\"1.20.1\",\"type\":\"release\"}]}");
            check.That(McDownloader.ResolveVersionId(manifest, "latest") == "1.20.1", "latest");
            check.That(McDownloader.ResolveVersionId(manifest, "latest-release") == "1.20.1", "latest-release");
            check.That(McDownloader.ResolveVersionId(manifest, "latest-snapshot") == "24w14a", "latest-snapshot");
            check.That(McDownloader.ResolveVersionId(manifest, "1.12.2") == "1.12.2", "explicit version");
            check.That(McDownloader.FindVersion(manifest, "1.20.1") != null, "find release");
            check.That(McDownloader.FindVersion(manifest, "missing") == null, "missing version");
            McDownloader.ValidateVersionId("1.12.2");
            McDownloader.ValidateVersionId("24w14a");
            McDownloader.ValidateVersionId("c0.0.13a");
            check.Passed += 3;
            check.Throws(delegate { McDownloader.ValidateVersionId("../1.12.2"); }, "reject dotdot");
            check.Throws(delegate { McDownloader.ValidateVersionId("a/b"); }, "reject slash");
            check.Throws(delegate { McDownloader.ValidateVersionId("a\\b"); }, "reject backslash");
            check.Throws(delegate { McDownloader.ValidateVersionId(""); }, "reject empty");
        }

        static void Libraries(Check check, string root)
        {
            var game = Path.Combine(root, "game-libs");
            Directory.CreateDirectory(game);
            Platform.Current = new PlatformInfo { OsArch = "x86_64", Arch = "64", NativeSuffix = "natives-windows", MirrorFirst = false };
            var tasks = McDownloader.BuildLibraryTasks(LibraryFixture(), game);
            check.That(tasks.Count == 5, "library count " + tasks.Count);
            check.That(Find(tasks, "com/example/ok/1.0/ok-1.0.jar") != null, "ok jar");
            check.That(Find(tasks, "lwjgl-3-natives-windows.jar") != null, "x64 natives");
            check.That(Find(tasks, "jinput-platform-2.0.5-natives-windows-64.jar") != null, "classifier natives");
            check.That(Find(tasks, "brigadier-1.0.17.jar") != null, "maven fallback");
            check.That(Find(tasks, "thing-1.2.jar") != null, "custom maven url");
            check.That(Find(tasks, "linux-1.0.jar") == null, "no linux library");
            check.That(Find(tasks, "natives-linux") == null, "no linux natives");
            check.That(Find(tasks, "natives-windows-arm64") == null, "no arm64 natives");
            check.That(Find(tasks, "natives-windows-x86.jar") == null, "no x86 natives");
            check.That(Find(tasks, "demo-1.0.jar") == null, "no demo library");
            check.That(Find(tasks, "outside.jar") == null, "no escaped library");

            var jinput = Find(tasks, "jinput-platform-2.0.5-natives-windows-64.jar");
            check.That(jinput.Kind == "natives" && jinput.Sha1 == "ee" && jinput.Size == 3, "jinput metadata");
            var ok = Find(tasks, "com/example/ok/1.0/ok-1.0.jar");
            check.That(ok.Urls.Length == 2 && ok.Urls[0].StartsWith("https://libraries.minecraft.net/", StringComparison.Ordinal), "ok official first");
            var thing = Find(tasks, "thing-1.2.jar");
            check.That(thing.Urls.Length == 1 && thing.Urls[0] == "https://example.com/maven/com/example/thing/1.2/thing-1.2.jar", "custom url");

            Platform.Current = new PlatformInfo { OsArch = "x86_64", Arch = "64", NativeSuffix = "natives-windows", MirrorFirst = true };
            var mirrored = McDownloader.BuildLibraryTasks(LibraryFixture(), game);
            var okMirror = Find(mirrored, "com/example/ok/1.0/ok-1.0.jar");
            check.That(okMirror.Urls[0] == "https://bmclapi2.bangbang93.com/maven/com/example/ok/1.0/ok-1.0.jar", "ok mirror first");
            check.That(okMirror.Urls[1] == "https://libraries.minecraft.net/com/example/ok/1.0/ok-1.0.jar", "ok official second");
        }

        static void Assets(Check check, string root)
        {
            var game = Path.Combine(root, "game-assets");
            Directory.CreateDirectory(game);
            var hash = new string('a', 40);
            var json = "{\"virtual\":true,\"map_to_resources\":true,\"objects\":{"
                + "\"icons/a.png\":{\"hash\":\"" + hash + "\",\"size\":4},"
                + "\"bad\":{\"hash\":\"../x\",\"size\":1},"
                + "\"short\":{\"hash\":\"abc\",\"size\":1}"
                + "}}";
            var plan = McDownloader.BuildAssetPlan(JsonUtil.ParseObject(json), game, false);
            check.That(plan.IsVirtual && plan.MapToResources, "legacy flags");
            check.That(plan.Tasks.Count == 1, "one asset task");
            check.That(plan.Copies.Count == 1 && plan.Copies[0].Name == "icons/a.png", "one copy");
            check.That(plan.Tasks[0].Sha1 == hash && plan.Tasks[0].Size == 4, "asset metadata");
            check.That(plan.Tasks[0].Urls[0] == "https://resources.download.minecraft.net/aa/" + hash, "asset url");
            check.That(Norm(plan.Tasks[0].Path).EndsWith("/aa/" + hash, StringComparison.Ordinal), "asset path");
            var empty = McDownloader.BuildAssetPlan(JsonUtil.ParseObject("{\"objects\":{}}"), game, false);
            check.That(empty.Tasks.Count == 0 && !empty.IsVirtual, "empty index");
        }

        static void Files(Check check, string root)
        {
            var file = Path.Combine(root, "hello.bin");
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("hello"));
            var sha = McDownloader.Sha1Hex(file);
            check.That(sha == "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", "known sha1");
            var task = new FileTask { Path = file, Sha1 = sha, Size = 5 };
            check.That(McDownloader.IsFileValid(task), "valid file");
            task.Sha1 = "0123456789abcdef0123456789abcdef01234567";
            check.That(!McDownloader.IsFileValid(task), "bad sha1");
            task.Sha1 = sha;
            task.Size = 1;
            check.That(!McDownloader.IsFileValid(task), "bad size");
            task.Size = 0;
            task.Sha1 = null;
            check.That(McDownloader.IsFileValid(task), "exists without sha1");
            task.Path = Path.Combine(root, "missing.bin");
            check.That(!McDownloader.IsFileValid(task), "missing file");
        }

        static void Settings(Check check, string root)
        {
            var path = Path.Combine(root, "mc-download.settings.json");
            SettingsStore.OverridePath = path;
            SettingsStore.SaveGameDir("D:\\Games\\.minecraft");
            check.That(File.Exists(path), "settings file");
            check.That(SettingsStore.LoadGameDir() == "D:\\Games\\.minecraft", "settings roundtrip");
            var bytes = File.ReadAllBytes(path);
            check.That(bytes.Length < 3 || bytes[0] != 0xEF, "settings without bom");
        }

        static void LegacyAndProfile(Check check, string root)
        {
            var game = Path.Combine(root, "legacy");
            var srcDir = Path.Combine(game, "assets", "objects", "aa");
            Directory.CreateDirectory(srcDir);
            var src = Path.Combine(srcDir, "hello.bin");
            File.WriteAllBytes(src, Encoding.ASCII.GetBytes("hello"));
            var copies = new List<AssetCopy>();
            copies.Add(new AssetCopy { Name = "icons/a.png", Src = src });
            copies.Add(new AssetCopy { Name = "../outside.txt", Src = src });
            var copied = McDownloader.CopyLegacyAssets(game, "legacy", true, true, copies);
            check.That(copied == 2, "copied to both legacy roots");
            var virtualFile = Path.Combine(game, "assets", "virtual", "legacy", "icons", "a.png");
            var resourceFile = Path.Combine(game, "resources", "icons", "a.png");
            check.That(File.ReadAllText(virtualFile) == "hello", "virtual copy");
            check.That(File.ReadAllText(resourceFile) == "hello", "resources copy");
            check.That(!File.Exists(Path.Combine(game, "outside.txt")), "did not escape game dir");
            check.That(!File.Exists(Path.Combine(Directory.GetParent(game).FullName, "outside.txt")), "did not escape parent");
            check.That(McDownloader.CopyLegacyAssets(game, "legacy", true, true, copies) == 0, "second copy skipped");

            check.That(McDownloader.WriteLauncherProfileIfMissing(game, "1.12.2"), "profile created");
            var profile = Path.Combine(game, "launcher_profiles.json");
            var text = File.ReadAllText(profile, Encoding.UTF8);
            check.That(text.IndexOf("\"lastVersionId\": \"1.12.2\"", StringComparison.Ordinal) >= 0, "profile id");
            check.That(text.IndexOf("\"version\": 3", StringComparison.Ordinal) >= 0, "profile version");
            var raw = File.ReadAllBytes(profile);
            check.That(raw.Length < 3 || raw[0] != 0xEF, "profile without bom");
            check.That(!McDownloader.WriteLauncherProfileIfMissing(game, "1.20.1"), "profile kept");
            var again = File.ReadAllText(profile, Encoding.UTF8);
            check.That(again.IndexOf("1.12.2", StringComparison.Ordinal) >= 0 && again.IndexOf("1.20.1", StringComparison.Ordinal) < 0, "profile not rewritten");
        }

        static void Http(Check check, string root)
        {
            var body = Encoding.ASCII.GetBytes("hello");
            var sha = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d";
            var listener = StartListener(out var port);
            var server = new Thread(delegate () { Serve(listener, body); });
            server.IsBackground = true;
            server.Start();
            var downloader = new McDownloader();
            downloader.RequestTimeoutMs = 3000;
            try
            {
                var good = "http://127.0.0.1:" + port + "/hello";
                var dead = "http://127.0.0.1:" + FreePort() + "/no";
                var file = Path.Combine(root, "http-hello.bin");
                var task = new FileTask { Urls = new[] { dead, good }, Path = file, Sha1 = sha, Size = 5, Kind = "asset" };
                var first = downloader.DownloadOne(task, CancellationToken.None);
                check.That(first.Status == FileStatus.Downloaded && first.Bytes == 5 && first.Url == good, "fallback download");
                check.That(File.ReadAllText(file) == "hello", "downloaded bytes");
                check.That(!File.Exists(file + ".part"), "part file removed");

                var many = new List<FileTask>();
                for (int i = 0; i < 4; i++)
                {
                    many.Add(new FileTask
                    {
                        Urls = new[] { good },
                        Path = Path.Combine(root, "parallel-" + i + ".bin"),
                        Sha1 = sha,
                        Size = 5,
                        Kind = "asset"
                    });
                }
                var batch = downloader.DownloadMany(many, "测试", 4, CancellationToken.None, null, null);
                check.That(batch.Files.Count == 4 && !batch.Canceled, "parallel count");
                for (int i = 0; i < batch.Files.Count; i++)
                    check.That(batch.Files[i].Status == FileStatus.Downloaded, "parallel file " + i);

                listener.Stop();
                var second = downloader.DownloadOne(task, CancellationToken.None);
                check.That(second.Status == FileStatus.Skipped && second.Bytes == 0, "sha1 skip");

                File.WriteAllBytes(file, Encoding.ASCII.GetBytes("hellp"));
                task.Urls = new[] { dead };
                var third = downloader.DownloadOne(task, CancellationToken.None);
                check.That(third.Status == FileStatus.Failed, "bad sha1 is not skipped");
                check.That(!File.Exists(file + ".part"), "failed part removed");
            }
            finally
            {
                try { listener.Close(); } catch (Exception) { }
                server.Join(2000);
            }
        }

        static void FormChecks(Check check)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(false, false))
            {
                check.That(form.Text == "原版 Minecraft 下载器", "window title");
                check.That(form.DownloadButtonText == "开始下载", "download button");
                check.That(!form.IncludeSnapshots, "snapshots off by default");
                check.That(form.LogIsReadOnlyMultiline, "log box");
                check.That(form.GameDirText == McDownloader.DefaultGameDir(), "default game dir");
                check.That(form.GameDirText.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase), "dir ends with .minecraft");
                form.LoadManifestJsonForTest("{\"latest\":{\"release\":\"1.20.1\",\"snapshot\":\"24w14a\"},\"versions\":[{\"id\":\"24w14a\",\"type\":\"snapshot\"},{\"id\":\"1.20.1\",\"type\":\"release\"},{\"id\":\"b1.7.3\",\"type\":\"old_beta\"}]}");
                check.That(form.VersionCount == 1, "releases only");
                check.That(form.SelectedVersionId == "1.20.1", "selected latest release");
                check.That(form.VersionAt(0).ToString().IndexOf("最新正式版", StringComparison.Ordinal) >= 0, "latest label");
                form.IncludeSnapshots = true;
                check.That(form.VersionCount == 3, "snapshots included");
                check.That(form.SelectedVersionId == "1.20.1", "selection kept");
                string beta = null;
                for (int i = 0; i < form.VersionCount; i++)
                {
                    if (form.VersionAt(i).Id == "b1.7.3") beta = form.VersionAt(i).ToString();
                }
                check.That(beta != null && beta.IndexOf("旧 beta", StringComparison.Ordinal) >= 0, "beta label");
            }
        }

        static Dictionary<string, object> LibraryFixture()
        {
            return JsonUtil.ParseObject(@"{
  ""libraries"": [
    {""name"":""com.example:ok:1.0"",""downloads"":{""artifact"":{""path"":""com/example/ok/1.0/ok-1.0.jar"",""url"":""https://libraries.minecraft.net/com/example/ok/1.0/ok-1.0.jar"",""sha1"":""aa"",""size"":1}}},
    {""name"":""com.example:linux:1.0"",""rules"":[{""action"":""allow"",""os"":{""name"":""linux""}}],""downloads"":{""artifact"":{""path"":""com/example/linux/1.0/linux-1.0.jar"",""url"":""https://libraries.minecraft.net/com/example/linux/1.0/linux-1.0.jar"",""sha1"":""bb"",""size"":1}}},
    {""name"":""org.lwjgl:lwjgl:3:natives-windows"",""rules"":[{""action"":""allow"",""os"":{""name"":""windows""}}],""downloads"":{""artifact"":{""path"":""org/lwjgl/lwjgl/3/lwjgl-3-natives-windows.jar"",""url"":""https://libraries.minecraft.net/org/lwjgl/lwjgl/3/lwjgl-3-natives-windows.jar"",""sha1"":""cc"",""size"":2}}},
    {""name"":""org.lwjgl:lwjgl:3:natives-windows-arm64"",""rules"":[{""action"":""allow"",""os"":{""name"":""windows"",""arch"":""arm64""}}],""downloads"":{""artifact"":{""path"":""org/lwjgl/lwjgl/3/lwjgl-3-natives-windows-arm64.jar"",""url"":""https://libraries.minecraft.net/arm.jar"",""sha1"":""dd"",""size"":2}}},
    {""name"":""org.lwjgl:lwjgl:3:natives-windows-x86"",""rules"":[{""action"":""allow"",""os"":{""name"":""windows"",""arch"":""x86""}}],""downloads"":{""artifact"":{""path"":""org/lwjgl/lwjgl/3/lwjgl-3-natives-windows-x86.jar"",""url"":""https://libraries.minecraft.net/x86.jar"",""sha1"":""d2"",""size"":2}}},
    {""name"":""net.java.jinput:jinput-platform:2.0.5"",""natives"":{""windows"":""natives-windows-${arch}"",""linux"":""natives-linux""},""downloads"":{""classifiers"":{""natives-windows-64"":{""path"":""net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows-64.jar"",""url"":""https://libraries.minecraft.net/jinput.jar"",""sha1"":""ee"",""size"":3},""natives-linux"":{""path"":""net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-linux.jar"",""url"":""https://libraries.minecraft.net/no.jar"",""sha1"":""ff"",""size"":3}}}},
    {""name"":""com.example:demo:1.0"",""rules"":[{""action"":""allow"",""features"":{""is_demo_user"":true}}],""downloads"":{""artifact"":{""path"":""com/example/demo/1.0/demo-1.0.jar"",""url"":""https://libraries.minecraft.net/demo.jar"",""sha1"":""11"",""size"":1}}},
    {""name"":""com.mojang:brigadier:1.0.17""},
    {""name"":""com.example:thing:1.2"",""url"":""https://example.com/maven/""},
    {""name"":""com.example:bad:1.0"",""downloads"":{""artifact"":{""path"":""../../outside.jar"",""url"":""https://libraries.minecraft.net/evil.jar"",""sha1"":""22"",""size"":1}}}
  ]
}");
        }

        static FileTask Find(List<FileTask> tasks, string fragment)
        {
            var needle = fragment.Replace('\\', '/').ToLowerInvariant();
            for (int i = 0; i < tasks.Count; i++)
            {
                if (Norm(tasks[i].Path).IndexOf(needle, StringComparison.Ordinal) >= 0) return tasks[i];
            }
            return null;
        }

        static string Norm(string path)
        {
            return (path ?? "").Replace('\\', '/').ToLowerInvariant();
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        static HttpListener StartListener(out int port)
        {
            for (int i = 0; i < 5; i++)
            {
                port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                    return listener;
                }
                catch (Exception)
                {
                    listener.Close();
                }
            }
            throw new InvalidOperationException("无法启动本地测试服务");
        }

        static void Serve(HttpListener listener, byte[] body)
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx = null;
                try { ctx = listener.GetContext(); }
                catch (Exception) { break; }
                try
                {
                    var missing = ctx.Request.Url.AbsolutePath.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0;
                    var data = missing ? Encoding.ASCII.GetBytes("missing") : body;
                    ctx.Response.StatusCode = missing ? 404 : 200;
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    try { ctx.Response.Abort(); }
                    catch (Exception) { }
                }
            }
        }

        sealed class Check
        {
            public int Passed;
            readonly IProgress<string> _log;

            public Check(IProgress<string> log)
            {
                _log = log;
            }

            public void That(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException("自测失败：" + message);
                Passed++;
                if (_log != null && Passed % 20 == 0) _log.Report("自测已通过 " + Passed + " 项");
            }

            public void Throws(Action action, string message)
            {
                var threw = false;
                try { action(); }
                catch (Exception) { threw = true; }
                That(threw, message);
            }
        }
    }
}

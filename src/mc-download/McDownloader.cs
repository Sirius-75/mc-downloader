using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace McDownload
{
    internal enum FileStatus
    {
        Failed = 0,
        Downloaded = 1,
        Skipped = 2
    }

    internal sealed class FileTask
    {
        public string[] Urls;
        public string Path;
        public string Sha1;
        public long Size;
        public string Kind;
    }

    internal sealed class FileResult
    {
        public string Path;
        public FileStatus Status;
        public long Bytes;
        public string Url;
        public string Error;
    }

    internal sealed class AssetCopy
    {
        public string Name;
        public string Src;
    }

    internal sealed class AssetPlan
    {
        public List<FileTask> Tasks = new List<FileTask>();
        public List<AssetCopy> Copies = new List<AssetCopy>();
        public bool IsVirtual;
        public bool MapToResources;
    }

    internal sealed class BatchResult
    {
        public List<FileResult> Files = new List<FileResult>();
        public bool Canceled;
    }

    internal struct TransferProgress
    {
        public string Stage;
        public int Completed;
        public int Total;
    }

    internal sealed class DownloadSummary
    {
        public string VersionId;
        public string GameDir;
        public int Total;
        public int Downloaded;
        public int Skipped;
        public int Failed;
        public double Megabytes;
        public double Seconds;
        public bool Canceled;
    }

    internal sealed class VersionEntry
    {
        public string Id;
        public string Type;
        public string Url;
        public string Sha1;
    }

    internal sealed class PlatformInfo
    {
        public string OsArch;
        public string Arch;
        public string NativeSuffix;
        public bool MirrorFirst;

        public static PlatformInfo Detect()
        {
            var archEnv = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            var archWow = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? "";
            string osArch;
            if (archEnv.Equals("ARM64", StringComparison.OrdinalIgnoreCase) || archWow.Equals("ARM64", StringComparison.OrdinalIgnoreCase))
                osArch = "arm64";
            else if (Environment.Is64BitOperatingSystem)
                osArch = "x86_64";
            else
                osArch = "x86";

            var bits = Environment.Is64BitOperatingSystem ? "64" : "32";
            var suffix = osArch == "arm64" ? "natives-windows-arm64" : (osArch == "x86" ? "natives-windows-x86" : "natives-windows");
            var culture = CultureInfo.CurrentCulture.Name ?? "";
            var offset = TimeZoneInfo.Local.BaseUtcOffset.TotalHours;
            var mirror = culture.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) || offset == 8d;
            return new PlatformInfo
            {
                OsArch = osArch,
                Arch = bits,
                NativeSuffix = suffix,
                MirrorFirst = mirror
            };
        }
    }

    internal static class Platform
    {
        public static PlatformInfo Current = PlatformInfo.Detect();
    }

    internal sealed class McDownloader
    {
        public const string Version = "2.0.0";
        public const string UserAgent = "mc-download/2.0.0";
        public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        public const int DefaultThreads = 8;
        public int RequestTimeoutMs = 600000;

        const string MirrorRoot = "https://bmclapi2.bangbang93.com";

        static readonly Regex MojangHost = new Regex(
            "^https://(piston-meta|piston-data|launchermeta|launcher)\\.mojang\\.com",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex LibrariesHost = new Regex(
            "^https://libraries\\.minecraft\\.net",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex AssetsHost = new Regex(
            "^https://resources\\.download\\.minecraft\\.net",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex NativeSuffixRegex = new Regex(
            ":(natives-windows(-x86|-arm64|-x86_64)?)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static McDownloader()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
            ServicePointManager.DefaultConnectionLimit = 256;
            ServicePointManager.Expect100Continue = false;
        }

        public static string DefaultGameDir()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft"));
        }

        public static string ResolveGameDir(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("请选择下载目录。");
            var trimmed = input.Trim().Trim('"');
            if (!Path.IsPathRooted(trimmed))
                trimmed = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, trimmed);
            try { return Path.GetFullPath(trimmed); }
            catch (Exception ex) { throw new InvalidOperationException("目录无效：" + ex.Message, ex); }
        }

        public Dictionary<string, object> FetchManifest(CancellationToken ct)
        {
            return JsonUtil.ParseObject(FetchString(ManifestUrl, ct));
        }

        public DownloadSummary Download(string version, string gameDir, int threads, CancellationToken ct, IProgress<string> log, IProgress<TransferProgress> progress)
        {
            threads = ClampThreads(threads);
            var mirrorFirst = Platform.Current.MirrorFirst;
            Write(log, "下载源：Auto（" + (mirrorFirst ? "BMCLAPI 镜像优先，失败回退官方" : "官方优先，失败回退 BMCLAPI") + "）");

            Dictionary<string, object> manifest;
            try
            {
                Write(log, "正在获取版本列表…");
                ct.ThrowIfCancellationRequested();
                manifest = JsonUtil.ParseObject(FetchString(ManifestUrl, ct));
            }
            catch (OperationCanceledException)
            {
                Write(log, "下载已取消。");
                return new DownloadSummary { Canceled = true, GameDir = gameDir };
            }

            var resolved = ResolveVersionId(manifest, version);
            ValidateVersionId(resolved);
            var entry = FindVersion(manifest, resolved);
            if (entry == null) throw new InvalidOperationException("找不到版本 '" + resolved + "'");

            var id = JsonUtil.Str(entry, "id");
            ValidateVersionId(id);
            gameDir = ResolveGameDir(gameDir);
            Directory.CreateDirectory(gameDir);
            Write(log, "目标版本：" + id + "（" + (JsonUtil.Str(entry, "type") ?? "") + "）  安装目录：" + gameDir);
            var swAll = Stopwatch.StartNew();
            var results = new List<FileResult>();

            var verDir = Path.Combine(gameDir, "versions", id);
            var jsonPath = Path.Combine(verDir, id + ".json");
            Write(log, "[1/6] 版本 json");
            var jsonTask = MakeTask(gameDir, JsonUtil.Str(entry, "url"), jsonPath, JsonUtil.Str(entry, "sha1"), 0, "json", mirrorFirst);
            if (jsonTask == null) throw new InvalidOperationException("版本 json 路径无效");

            FileResult jsonResult;
            try { jsonResult = DownloadOne(jsonTask, ct); }
            catch (OperationCanceledException)
            {
                return Finish(results, id, gameDir, swAll, true, log);
            }
            results.Add(jsonResult);
            if (jsonResult.Status == FileStatus.Failed)
                throw new InvalidOperationException("版本 json 下载失败：" + jsonResult.Error);

            var versionJson = JsonUtil.ParseObject(File.ReadAllText(jsonPath, Encoding.UTF8));
            if (versionJson.ContainsKey("inheritsFrom") && versionJson["inheritsFrom"] != null)
                Write(log, "警告：该 json 继承自 " + JsonUtil.Str(versionJson, "inheritsFrom") + "，本程序只处理原版");

            var tasks = new List<FileTask>();
            var client = JsonUtil.Child(JsonUtil.Child(versionJson, "downloads"), "client");
            if (client == null || string.IsNullOrEmpty(JsonUtil.Str(client, "url")))
                throw new InvalidOperationException("版本 json 缺少客户端下载信息");
            var clientTask = MakeTask(
                gameDir,
                JsonUtil.Str(client, "url"),
                Path.Combine(verDir, id + ".jar"),
                JsonUtil.Str(client, "sha1"),
                JsonUtil.Long(client, "size"),
                "client",
                mirrorFirst);
            if (clientTask == null) throw new InvalidOperationException("客户端路径无效");
            tasks.Add(clientTask);

            var libTasks = BuildLibraryTasks(versionJson, gameDir);
            tasks.AddRange(libTasks);

            var assetIndex = JsonUtil.Child(versionJson, "assetIndex");
            if (assetIndex == null || string.IsNullOrEmpty(JsonUtil.Str(assetIndex, "url")))
                throw new InvalidOperationException("版本 json 缺少资源索引");
            var indexId = JsonUtil.Str(assetIndex, "id");
            ValidateVersionId(indexId);
            var indexPath = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            var indexTask = MakeTask(
                gameDir,
                JsonUtil.Str(assetIndex, "url"),
                indexPath,
                JsonUtil.Str(assetIndex, "sha1"),
                JsonUtil.Long(assetIndex, "size"),
                "assetIndex",
                mirrorFirst);
            if (indexTask == null) throw new InvalidOperationException("资源索引路径无效");
            tasks.Add(indexTask);

            var logging = JsonUtil.Child(versionJson, "logging");
            var clientLog = logging == null ? null : JsonUtil.Child(logging, "client");
            var logFile = clientLog == null ? null : JsonUtil.Child(clientLog, "file");
            if (logFile != null && !string.IsNullOrEmpty(JsonUtil.Str(logFile, "url")))
            {
                var logId = JsonUtil.Str(logFile, "id");
                if (!string.IsNullOrEmpty(logId))
                {
                    ValidateVersionId(logId);
                    var logTask = MakeTask(
                        gameDir,
                        JsonUtil.Str(logFile, "url"),
                        Path.Combine(gameDir, "assets", "log_configs", logId),
                        JsonUtil.Str(logFile, "sha1"),
                        JsonUtil.Long(logFile, "size"),
                        "logging",
                        mirrorFirst);
                    if (logTask != null) tasks.Add(logTask);
                }
            }

            int natives = 0;
            for (int i = 0; i < libTasks.Count; i++)
            {
                if (libTasks[i].Kind == "natives") natives++;
            }
            Write(log, "[2/6] 客户端 jar、支持库（" + libTasks.Count + " 个，其中 natives " + natives + " 个）、资源索引、日志配置");
            var libraries = DownloadMany(tasks, "本体与支持库", threads, ct, log, progress);
            results.AddRange(libraries.Files);
            if (libraries.Canceled) return Finish(results, id, gameDir, swAll, true, log);
            if (!File.Exists(indexPath)) throw new InvalidOperationException("资源索引下载失败，无法继续");

            var index = JsonUtil.ParseObject(File.ReadAllText(indexPath, Encoding.UTF8));
            var plan = BuildAssetPlan(index, gameDir, mirrorFirst);
            Write(log, "[3/6] 资源文件（索引 " + indexId + "，" + plan.Tasks.Count + " 个对象）");
            var assets = DownloadMany(plan.Tasks, "资源文件", threads, ct, log, progress);
            results.AddRange(assets.Files);
            if (assets.Canceled) return Finish(results, id, gameDir, swAll, true, log);

            if (plan.Copies.Count > 0 && (plan.IsVirtual || plan.MapToResources))
            {
                var targets = new List<string>();
                if (plan.IsVirtual) targets.Add(Path.Combine(gameDir, "assets", "virtual", indexId));
                if (plan.MapToResources) targets.Add(Path.Combine(gameDir, "resources"));
                Write(log, "[4/6] 旧版资源：复制 " + plan.Copies.Count + " 个文件到 " + string.Join("、", targets.ToArray()));
                CopyLegacyAssets(gameDir, indexId, plan.IsVirtual, plan.MapToResources, plan.Copies);
            }
            else
            {
                Write(log, "[4/6] 非旧版资源格式，无需复制");
            }

            if (WriteLauncherProfileIfMissing(gameDir, id)) Write(log, "[5/6] 创建 launcher_profiles.json");
            else Write(log, "[5/6] launcher_profiles.json 已存在，保持不变");
            return Finish(results, id, gameDir, swAll, false, log);
        }

        internal BatchResult DownloadMany(IList<FileTask> tasks, string title, int threads, CancellationToken ct, IProgress<string> log, IProgress<TransferProgress> progress)
        {
            var batch = new BatchResult();
            if (tasks == null || tasks.Count == 0) return batch;
            threads = ClampThreads(threads);
            Write(log, string.Format(CultureInfo.InvariantCulture, "[{0}] 共 {1} 个文件，使用 {2} 个线程", title, tasks.Count, threads));

            var pump = new ProgressPump();
            pump.Progress = progress;
            pump.Stage = title;
            pump.Total = tasks.Count;
            pump.Report(0);

            var bag = new ConcurrentBag<FileResult>();
            int done = 0;
            var sw = Stopwatch.StartNew();
            var sem = new SemaphoreSlim(threads, threads);
            var workers = new List<Task>();
            bool canceled = false;
            try
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    sem.Wait(ct);
                    var captured = tasks[i];
                    try
                    {
                        workers.Add(Task.Run(delegate
                        {
                            try
                            {
                                var one = DownloadOne(captured, ct);
                                bag.Add(one);
                                pump.Report(Interlocked.Increment(ref done));
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                bag.Add(new FileResult { Path = captured.Path, Status = FileStatus.Failed, Error = ex.Message });
                                pump.Report(Interlocked.Increment(ref done));
                            }
                            finally
                            {
                                sem.Release();
                            }
                        }));
                    }
                    catch
                    {
                        sem.Release();
                        throw;
                    }
                }
                if (workers.Count > 0) Task.WaitAll(workers.ToArray());
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (AggregateException ex)
            {
                if (ct.IsCancellationRequested || AllCanceled(ex)) canceled = true;
                else throw;
            }

            if (workers.Count > 0)
            {
                try { Task.WaitAll(workers.ToArray()); }
                catch (Exception) { }
            }
            sem.Dispose();
            if (ct.IsCancellationRequested) canceled = true;

            batch.Files.AddRange(bag);
            batch.Canceled = canceled;
            int downloaded = 0;
            int skipped = 0;
            int failed = 0;
            long bytes = 0;
            for (int i = 0; i < batch.Files.Count; i++)
            {
                var file = batch.Files[i];
                if (file.Status == FileStatus.Downloaded) { downloaded++; bytes += file.Bytes; }
                else if (file.Status == FileStatus.Skipped) skipped++;
                else failed++;
            }
            Write(log, string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] 完成：新下载 {1}，已存在跳过 {2}，失败 {3}，{4:N1} MB，用时 {5:N1} 秒",
                title,
                downloaded,
                skipped,
                failed,
                bytes / 1048576.0,
                sw.Elapsed.TotalSeconds));
            if (canceled) Write(log, "[" + title + "] 已取消");
            return batch;
        }

        internal FileResult DownloadOne(FileTask task, CancellationToken ct)
        {
            var result = new FileResult { Path = task.Path, Status = FileStatus.Failed };
            try
            {
                if (IsFileValid(task))
                {
                    result.Status = FileStatus.Skipped;
                    return result;
                }
            }
            catch (Exception) { }

            var parent = Path.GetDirectoryName(task.Path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            var tmp = task.Path + ".part";
            if (task.Urls == null || task.Urls.Length == 0)
            {
                result.Error = "没有可用的下载地址";
                return result;
            }

            var errors = new List<string>();
            for (int round = 1; round <= 3; round++)
            {
                for (int i = 0; i < task.Urls.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var url = task.Urls[i];
                    try
                    {
                        DownloadToFile(url, tmp, ct);
                        var len = new FileInfo(tmp).Length;
                        if (task.Size > 0 && len != task.Size)
                            throw new InvalidOperationException("大小不符：期望 " + task.Size + "，实际 " + len);
                        if (!string.IsNullOrEmpty(task.Sha1))
                        {
                            var hash = Sha1Hex(tmp);
                            if (!hash.Equals(task.Sha1, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("SHA1 不符：期望 " + task.Sha1 + "，实际 " + hash);
                        }
                        if (File.Exists(task.Path)) File.Delete(task.Path);
                        File.Move(tmp, task.Path);
                        result.Status = FileStatus.Downloaded;
                        result.Bytes = len;
                        result.Url = url;
                        result.Error = null;
                        return result;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(tmp);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TryDelete(tmp);
                        errors.Add("[" + round + "] " + url + " -> " + ex.Message);
                        if (ct.WaitHandle.WaitOne(300 * round))
                            throw new OperationCanceledException(ct);
                    }
                }
            }

            result.Error = string.Join("; ", errors.ToArray());
            if (result.Error != null && result.Error.Length > 800)
                result.Error = result.Error.Substring(0, 800);
            return result;
        }

        internal static bool IsFileValid(FileTask task)
        {
            if (task == null || string.IsNullOrEmpty(task.Path) || !File.Exists(task.Path)) return false;
            var info = new FileInfo(task.Path);
            if (task.Size > 0 && info.Length != task.Size) return false;
            if (!string.IsNullOrEmpty(task.Sha1))
                return Sha1Hex(task.Path).Equals(task.Sha1, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        internal static string Sha1Hex(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        internal static string MirrorUrl(string url)
        {
            var upgraded = UpgradeToHttps(url);
            if (string.IsNullOrEmpty(upgraded)) return upgraded;
            var mirrored = MojangHost.Replace(upgraded, MirrorRoot);
            mirrored = LibrariesHost.Replace(mirrored, MirrorRoot + "/maven");
            mirrored = AssetsHost.Replace(mirrored, MirrorRoot + "/assets");
            return mirrored;
        }

        internal static string[] SourceUrls(string url, bool mirrorFirst)
        {
            var official = UpgradeToHttps(url);
            if (string.IsNullOrEmpty(official)) return new string[0];
            var mirror = MirrorUrl(official);
            if (string.Equals(mirror, official, StringComparison.OrdinalIgnoreCase))
                return new[] { official };
            if (mirrorFirst) return new[] { mirror, official };
            return new[] { official, mirror };
        }

        internal static bool RulesAllow(object rules, string osArch)
        {
            if (rules == null) return true;
            var list = rules as IList;
            if (list == null) return false;
            bool allow = false;
            foreach (var item in list)
            {
                var rule = item as Dictionary<string, object>;
                if (rule == null) continue;
                bool match = true;
                if (rule.ContainsKey("features")) match = false;
                if (match)
                {
                    object osRaw;
                    if (rule.TryGetValue("os", out osRaw) && osRaw != null)
                    {
                        var os = osRaw as Dictionary<string, object>;
                        if (os != null)
                        {
                            var name = JsonUtil.Str(os, "name");
                            if (!string.IsNullOrEmpty(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                                match = false;
                            var arch = JsonUtil.Str(os, "arch");
                            if (!string.IsNullOrEmpty(arch) && !arch.Equals(osArch ?? "", StringComparison.OrdinalIgnoreCase))
                                match = false;
                        }
                    }
                }
                if (match)
                {
                    var action = JsonUtil.Str(rule, "action");
                    allow = action != null && action.Equals("allow", StringComparison.OrdinalIgnoreCase);
                }
            }
            return allow;
        }

        internal static string MavenPath(string name, string classifier)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("无法解析库名");
            var parts = name.Split(':');
            var ext = "jar";
            var last = parts[parts.Length - 1];
            var at = last.IndexOf('@');
            if (at >= 0)
            {
                ext = last.Substring(at + 1);
                parts[parts.Length - 1] = last.Substring(0, at);
            }
            if (parts.Length < 3) throw new InvalidOperationException("无法解析库名：" + name);
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var ver = parts[2];
            var cls = "";
            if (!string.IsNullOrEmpty(classifier)) cls = "-" + classifier;
            else if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3])) cls = "-" + parts[3];
            return group + "/" + artifact + "/" + ver + "/" + artifact + "-" + ver + cls + "." + ext;
        }

        internal static void ValidateVersionId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("版本号为空");
            if (id == "." || id == ".."
                || id.IndexOf("..", StringComparison.Ordinal) >= 0
                || id.IndexOf('/') >= 0
                || id.IndexOf('\\') >= 0
                || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("非法版本号：" + id);
        }

        internal static string ResolveVersionId(Dictionary<string, object> manifest, string version)
        {
            if (string.IsNullOrWhiteSpace(version)
                || version.Equals("latest", StringComparison.OrdinalIgnoreCase)
                || version.Equals("latest-release", StringComparison.OrdinalIgnoreCase))
                return JsonUtil.Str(JsonUtil.Child(manifest, "latest"), "release");
            if (version.Equals("latest-snapshot", StringComparison.OrdinalIgnoreCase))
                return JsonUtil.Str(JsonUtil.Child(manifest, "latest"), "snapshot");
            return version.Trim();
        }

        internal static Dictionary<string, object> FindVersion(Dictionary<string, object> manifest, string id)
        {
            var versions = JsonUtil.ObjectList(manifest, "versions");
            if (versions == null) return null;
            for (int i = 0; i < versions.Count; i++)
            {
                if (string.Equals(JsonUtil.Str(versions[i], "id"), id, StringComparison.Ordinal))
                    return versions[i];
            }
            return null;
        }

        internal static List<VersionEntry> ListVersions(Dictionary<string, object> manifest)
        {
            var list = new List<VersionEntry>();
            var versions = JsonUtil.ObjectList(manifest, "versions");
            if (versions == null) return list;
            for (int i = 0; i < versions.Count; i++)
            {
                var id = JsonUtil.Str(versions[i], "id");
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new VersionEntry
                {
                    Id = id,
                    Type = JsonUtil.Str(versions[i], "type"),
                    Url = JsonUtil.Str(versions[i], "url"),
                    Sha1 = JsonUtil.Str(versions[i], "sha1")
                });
            }
            return list;
        }

        internal static string LatestId(Dictionary<string, object> manifest, string channel)
        {
            return JsonUtil.Str(JsonUtil.Child(manifest, "latest"), channel);
        }

        internal static List<FileTask> BuildLibraryTasks(Dictionary<string, object> versionJson, string gameDir)
        {
            var map = new Dictionary<string, FileTask>(StringComparer.OrdinalIgnoreCase);
            var libs = versionJson == null ? null : JsonUtil.ObjectList(versionJson, "libraries");
            if (libs == null) return new List<FileTask>();
            var libDir = Path.Combine(gameDir, "libraries");
            var platform = Platform.Current;
            for (int i = 0; i < libs.Count; i++)
            {
                var lib = libs[i];
                object rules = null;
                object rulesRaw;
                if (lib.TryGetValue("rules", out rulesRaw)) rules = rulesRaw;
                if (!RulesAllow(rules, platform.OsArch)) continue;

                var name = JsonUtil.Str(lib, "name");
                if (SkipNativeName(name, platform.NativeSuffix)) continue;

                var downloads = JsonUtil.Child(lib, "downloads");
                var artifact = downloads == null ? null : JsonUtil.Child(downloads, "artifact");
                var artifactUrl = JsonUtil.Str(artifact, "url");
                if (downloads != null && artifact != null && !string.IsNullOrEmpty(artifactUrl))
                {
                    var rel = JsonUtil.Str(artifact, "path");
                    if (string.IsNullOrEmpty(rel)) rel = MavenPath(name, null);
                    AddLibrary(map, gameDir, libDir, rel, artifactUrl, JsonUtil.Str(artifact, "sha1"), JsonUtil.Long(artifact, "size"), "library", platform.MirrorFirst);
                }
                else if (downloads == null && !string.IsNullOrEmpty(name) && !HasNatives(lib))
                {
                    var baseUrl = JsonUtil.Str(lib, "url");
                    if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://libraries.minecraft.net";
                    baseUrl = baseUrl.TrimEnd('/');
                    var rel = MavenPath(name, null);
                    AddLibrary(map, gameDir, libDir, rel, baseUrl + "/" + rel, null, 0, "library", platform.MirrorFirst);
                }

                var natives = JsonUtil.Child(lib, "natives");
                var windowsNative = JsonUtil.Str(natives, "windows");
                if (string.IsNullOrEmpty(windowsNative)) continue;
                var classifier = windowsNative.Replace("${arch}", platform.Arch);
                Dictionary<string, object> classified = null;
                var classifiers = downloads == null ? null : JsonUtil.Child(downloads, "classifiers");
                if (classifiers != null)
                {
                    object raw;
                    if (classifiers.TryGetValue(classifier, out raw)) classified = raw as Dictionary<string, object>;
                }
                if (classified != null)
                {
                    var rel = JsonUtil.Str(classified, "path");
                    if (string.IsNullOrEmpty(rel)) rel = string.IsNullOrEmpty(name) ? null : MavenPath(name, classifier);
                    if (!string.IsNullOrEmpty(rel))
                        AddLibrary(map, gameDir, libDir, rel, JsonUtil.Str(classified, "url"), JsonUtil.Str(classified, "sha1"), JsonUtil.Long(classified, "size"), "natives", platform.MirrorFirst);
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    var baseUrl = JsonUtil.Str(lib, "url");
                    if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://libraries.minecraft.net";
                    baseUrl = baseUrl.TrimEnd('/');
                    var rel = MavenPath(name, classifier);
                    AddLibrary(map, gameDir, libDir, rel, baseUrl + "/" + rel, null, 0, "natives", platform.MirrorFirst);
                }
            }
            return new List<FileTask>(map.Values);
        }

        internal static AssetPlan BuildAssetPlan(Dictionary<string, object> index, string gameDir, bool mirrorFirst)
        {
            var plan = new AssetPlan();
            if (index == null) return plan;
            plan.IsVirtual = JsonUtil.Bool(index, "virtual");
            plan.MapToResources = JsonUtil.Bool(index, "map_to_resources");
            var objects = JsonUtil.Child(index, "objects");
            if (objects == null) return plan;

            var objDir = Path.Combine(gameDir, "assets", "objects");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in objects)
            {
                var meta = pair.Value as Dictionary<string, object>;
                var hash = JsonUtil.Str(meta, "hash");
                if (!IsSha1(hash)) continue;
                var full = Path.GetFullPath(Path.Combine(objDir, hash.Substring(0, 2), hash));
                if (!IsUnderDirectory(gameDir, full)) continue;
                if (seen.Add(full))
                {
                    var url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash;
                    plan.Tasks.Add(new FileTask
                    {
                        Urls = SourceUrls(url, mirrorFirst),
                        Path = full,
                        Sha1 = hash,
                        Size = JsonUtil.Long(meta, "size"),
                        Kind = "asset"
                    });
                }
                if (plan.IsVirtual || plan.MapToResources)
                    plan.Copies.Add(new AssetCopy { Name = pair.Key, Src = full });
            }
            return plan;
        }

        internal static int CopyLegacyAssets(string gameDir, string indexId, bool isVirtual, bool mapToResources, IList<AssetCopy> copies)
        {
            if (copies == null || copies.Count == 0) return 0;
            var targets = new List<string>();
            if (isVirtual) targets.Add(Path.Combine(gameDir, "assets", "virtual", indexId));
            if (mapToResources) targets.Add(Path.Combine(gameDir, "resources"));
            int copied = 0;
            for (int t = 0; t < targets.Count; t++)
            {
                var root = Path.GetFullPath(targets[t]);
                Directory.CreateDirectory(root);
                for (int i = 0; i < copies.Count; i++)
                {
                    var item = copies[i];
                    if (item == null || string.IsNullOrEmpty(item.Src) || !File.Exists(item.Src)) continue;
                    var rel = (item.Name ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var dst = Path.GetFullPath(Path.Combine(root, rel));
                    if (!IsUnderDirectory(root, dst)) continue;
                    var srcLen = new FileInfo(item.Src).Length;
                    if (File.Exists(dst) && new FileInfo(dst).Length == srcLen) continue;
                    var parent = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.Copy(item.Src, dst, true);
                    copied++;
                }
            }
            return copied;
        }

        internal static bool WriteLauncherProfileIfMissing(string gameDir, string versionId)
        {
            Directory.CreateDirectory(gameDir);
            var path = Path.Combine(gameDir, "launcher_profiles.json");
            if (File.Exists(path)) return false;
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var id = EscapeJson(versionId);
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"profiles\": {\r\n");
            sb.Append("    \"mcdl-").Append(id).Append("\": {\r\n");
            sb.Append("      \"name\": \"").Append(id).Append("\",\r\n");
            sb.Append("      \"type\": \"custom\",\r\n");
            sb.Append("      \"lastVersionId\": \"").Append(id).Append("\",\r\n");
            sb.Append("      \"created\": \"").Append(now).Append("\",\r\n");
            sb.Append("      \"lastUsed\": \"").Append(now).Append("\",\r\n");
            sb.Append("      \"icon\": \"Grass\"\r\n");
            sb.Append("    }\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"settings\": {},\r\n");
            sb.Append("  \"version\": 3\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        internal static bool IsUnderDirectory(string root, string candidate)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;
            var baseFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(candidate);
            var prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        static DownloadSummary Finish(List<FileResult> results, string id, string gameDir, Stopwatch sw, bool canceled, IProgress<string> log)
        {
            var summary = Summarize(results, id, gameDir, sw, canceled);
            Write(log, "");
            Write(log, "[6/6] ========== 汇总 ==========");
            Write(log, "版本：" + id + "    目录：" + gameDir);
            Write(log, "文件总数：" + summary.Total + "    新下载：" + summary.Downloaded + "    已存在且校验通过：" + summary.Skipped + "    失败：" + summary.Failed);
            Write(log, string.Format(CultureInfo.InvariantCulture, "下载量：{0:N1} MB    总用时：{1:N1} 秒", summary.Megabytes, summary.Seconds));
            int shown = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Status != FileStatus.Failed) continue;
                if (shown >= 10) break;
                Write(log, "失败：" + results[i].Path);
                Write(log, "  " + (results[i].Error ?? ""));
                shown++;
            }
            if (summary.Failed > shown) Write(log, "另有 " + (summary.Failed - shown) + " 个失败未列出。");
            if (canceled) Write(log, "下载已取消。");
            else if (summary.Failed > 0) Write(log, "有 " + summary.Failed + " 个文件失败，重新运行即可只补下缺失文件。");
            else Write(log, "全部文件 SHA1 校验通过。可以用 PCL / HMCL / 官方启动器选择该 .minecraft 文件夹启动（需要已安装 Java）。");
            return summary;
        }

        static DownloadSummary Summarize(List<FileResult> results, string id, string gameDir, Stopwatch sw, bool canceled)
        {
            var summary = new DownloadSummary();
            summary.VersionId = id;
            summary.GameDir = gameDir;
            summary.Canceled = canceled;
            summary.Total = results.Count;
            long bytes = 0;
            for (int i = 0; i < results.Count; i++)
            {
                var file = results[i];
                if (file.Status == FileStatus.Downloaded) { summary.Downloaded++; bytes += file.Bytes; }
                else if (file.Status == FileStatus.Skipped) summary.Skipped++;
                else summary.Failed++;
            }
            summary.Megabytes = bytes / 1048576.0;
            summary.Seconds = sw.Elapsed.TotalSeconds;
            return summary;
        }

        string FetchString(string url, CancellationToken ct)
        {
            var errors = new List<string>();
            var urls = SourceUrls(url, Platform.Current.MirrorFirst);
            for (int u = 0; u < urls.Length; u++)
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using (var client = NewClient())
                        using (ct.Register(delegate { try { client.CancelAsync(); } catch (Exception) { } }))
                        {
                            string text;
                            try { text = client.DownloadString(urls[u]); }
                            catch (WebException ex)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                throw new InvalidOperationException(ex.Message, ex);
                            }
                            if (!string.IsNullOrEmpty(text) && text[0] == '\uFEFF') text = text.Substring(1);
                            return text;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(urls[u] + " -> " + ex.Message);
                        if (ct.WaitHandle.WaitOne(300 * attempt))
                            throw new OperationCanceledException(ct);
                    }
                }
            }
            throw new InvalidOperationException("获取 " + url + " 失败：" + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
        }

        void DownloadToFile(string url, string destination, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var client = NewClient())
            using (ct.Register(delegate { try { client.CancelAsync(); } catch (Exception) { } }))
            {
                try { client.DownloadFile(url, destination); }
                catch (WebException ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }
        }

        TimeoutWebClient NewClient()
        {
            var client = new TimeoutWebClient();
            client.TimeoutMs = RequestTimeoutMs;
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            return client;
        }

        static FileTask MakeTask(string gameDir, string url, string path, string sha1, long size, string kind, bool mirrorFirst)
        {
            var full = Path.GetFullPath(path);
            if (!IsUnderDirectory(gameDir, full)) return null;
            return new FileTask
            {
                Urls = SourceUrls(url, mirrorFirst),
                Path = full,
                Sha1 = sha1,
                Size = size,
                Kind = kind
            };
        }

        static void AddLibrary(Dictionary<string, FileTask> map, string gameDir, string libDir, string relative, string url, string sha1, long size, string kind, bool mirrorFirst)
        {
            if (string.IsNullOrEmpty(relative)) return;
            var rel = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            while (rel.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                rel = rel.Substring(1);
            var full = Path.GetFullPath(Path.Combine(libDir, rel));
            if (!IsUnderDirectory(gameDir, full)) return;
            map[full] = new FileTask
            {
                Urls = SourceUrls(url, mirrorFirst),
                Path = full,
                Sha1 = sha1,
                Size = size,
                Kind = kind
            };
        }

        static bool HasNatives(Dictionary<string, object> lib)
        {
            object raw;
            return lib.TryGetValue("natives", out raw) && raw != null;
        }

        static bool SkipNativeName(string name, string nativeSuffix)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var match = NativeSuffixRegex.Match(name);
            if (!match.Success) return false;
            return !string.Equals(match.Groups[1].Value, nativeSuffix, StringComparison.Ordinal);
        }

        static bool IsSha1(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 40) return false;
            for (int i = 0; i < hash.Length; i++)
            {
                var c = hash[i];
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        static string UpgradeToHttps(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + url.Substring("http://".Length);
            return url;
        }

        static string EscapeJson(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static int ClampThreads(int threads)
        {
            if (threads < 1) return 1;
            if (threads > 64) return 64;
            return threads;
        }

        static void Write(IProgress<string> log, string message)
        {
            if (log != null) log.Report(message);
        }

        static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        static bool AllCanceled(AggregateException ex)
        {
            var flat = ex.Flatten();
            if (flat.InnerExceptions.Count == 0) return false;
            for (int i = 0; i < flat.InnerExceptions.Count; i++)
            {
                if (!(flat.InnerExceptions[i] is OperationCanceledException)) return false;
            }
            return true;
        }

        sealed class ProgressPump
        {
            public IProgress<TransferProgress> Progress;
            public string Stage;
            public int Total;
            int _last;
            readonly object _gate = new object();

            public void Report(int completed)
            {
                if (Progress == null || Total <= 0) return;
                var now = Environment.TickCount;
                lock (_gate)
                {
                    if (completed != Total && completed != 0 && unchecked(now - _last) < 200) return;
                    _last = now;
                }
                Progress.Report(new TransferProgress { Stage = Stage, Completed = completed, Total = Total });
            }
        }

        sealed class TimeoutWebClient : WebClient
        {
            public int TimeoutMs = 600000;

            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = TimeoutMs;
                    request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                    var http = request as HttpWebRequest;
                    if (http != null) http.ReadWriteTimeout = TimeoutMs;
                }
                return request;
            }
        }
    }
}

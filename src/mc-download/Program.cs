using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace McDownload
{
    internal static class Program
    {
        const int AttachParentProcess = -1;
        const int StdOutputHandle = -11;
        const int StdErrorHandle = -12;

        static StreamWriter _stdout;
        static StreamWriter _stderr;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [STAThread]
        static void Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
                {
                    try { MessageBox.Show(e.Exception.Message, "原版 Minecraft 下载器", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch (Exception) { }
                };
                Application.Run(new MainForm());
                return;
            }

            TryAttachConsole();
            var logPath = Value(args, "--log");
            int code;
            using (var sink = new LogSink(logPath))
            {
                try { code = Cli(args, sink); }
                catch (Exception ex)
                {
                    sink.Report(ex.Message);
                    code = 1;
                }
            }
            // WinExe: exit explicitly so a GUI-subsystem process reports the code
            // even if a leftover thread would otherwise keep the process alive.
            Environment.Exit(code);
        }

        static int Cli(string[] args, LogSink sink)
        {
            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
            {
                sink.Report(HelpText());
                return 0;
            }
            if (Has(args, "--version"))
            {
                sink.Report("mc-download " + ProductVersion());
                return 0;
            }
            if (Has(args, "--self-test"))
            {
                int count = SelfTest.Run(sink);
                sink.Report("自测通过：" + count + " 项");
                return 0;
            }
            if (Has(args, "--download"))
            {
                var version = Value(args, "--download");
                if (string.IsNullOrWhiteSpace(version))
                {
                    sink.Report("请指定版本，例如 --download 1.12.2");
                    return 2;
                }
                var dir = Value(args, "--dir");
                if (string.IsNullOrWhiteSpace(dir)) dir = McDownloader.DefaultGameDir();
                dir = McDownloader.ResolveGameDir(dir);
                var threads = McDownloader.DefaultThreads;
                var threadText = Value(args, "--threads");
                if (!string.IsNullOrWhiteSpace(threadText))
                {
                    int parsed;
                    if (!int.TryParse(threadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        sink.Report("线程数无效");
                        return 2;
                    }
                    threads = parsed;
                }
                var transfer = new ActionProgress<TransferProgress>(delegate (TransferProgress p)
                {
                    sink.Report(p.Stage + "  " + p.Completed + "/" + p.Total);
                });
                var summary = new McDownloader().Download(version, dir, threads, CancellationToken.None, sink, transfer);
                if (summary.Canceled) return 2;
                if (summary.Failed > 0) return 1;
                return 0;
            }
            sink.Report("无法识别的参数。双击运行打开界面，或使用 --help。");
            return 2;
        }

        static string HelpText()
        {
            return "原版 Minecraft 下载器 " + McDownloader.Version + "\r\n"
                + "\r\n"
                + "不带参数：打开图形界面。\r\n"
                + "\r\n"
                + "--download <版本>   下载指定版本，也可用 latest / latest-snapshot\r\n"
                + "--dir <目录>        .minecraft 目录，默认是程序旁边的 .minecraft\r\n"
                + "--threads <1-64>    线程数，默认 8\r\n"
                + "--log <文件>        把日志额外写入文件\r\n"
                + "--self-test         运行自测\r\n"
                + "--version           显示版本号\r\n"
                + "--help              显示本说明";
        }

        static string ProductVersion()
        {
            var info = FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location);
            var version = info.ProductVersion;
            if (string.IsNullOrEmpty(version)) version = info.FileVersion;
            if (string.IsNullOrEmpty(version)) return McDownloader.Version;
            var plus = version.IndexOf('+');
            if (plus >= 0) version = version.Substring(0, plus);
            return version;
        }

        static bool Has(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null) continue;
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string Value(string[] args, string name)
        {
            var prefix = name + "=";
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null) continue;
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return arg.Substring(prefix.Length);
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }

        static void TryAttachConsole()
        {
            try
            {
                if (!AttachConsole(AttachParentProcess)) return;
                _stdout = OpenHandle(StdOutputHandle);
                _stderr = OpenHandle(StdErrorHandle);
                if (_stdout != null) Console.SetOut(_stdout);
                if (_stderr != null) Console.SetError(_stderr);
            }
            catch (Exception) { }
        }

        static StreamWriter OpenHandle(int which)
        {
            var handle = GetStdHandle(which);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            var stream = new FileStream(new SafeFileHandle(handle, false), FileAccess.Write);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    internal sealed class LogSink : IProgress<string>, IDisposable
    {
        readonly object _gate = new object();
        readonly StreamWriter _file;

        public LogSink(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            _file = new StreamWriter(full, false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Report(string value)
        {
            lock (_gate)
            {
                try { Console.WriteLine(value); }
                catch (Exception) { }
                if (_file != null) _file.WriteLine(value);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_file != null) _file.Dispose();
            }
        }
    }

    internal sealed class ActionProgress<T> : IProgress<T>
    {
        readonly Action<T> _action;

        public ActionProgress(Action<T> action)
        {
            _action = action;
        }

        public void Report(T value)
        {
            _action(value);
        }
    }
}

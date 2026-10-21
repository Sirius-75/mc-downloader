using System;
using System.IO;
using System.Text;

namespace McDownload
{
    internal static class SettingsStore
    {
        internal static string OverridePath;

        public static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(OverridePath)) return OverridePath;
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mc-download.settings.json");
            }
        }

        public static string LoadGameDir()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var obj = JsonUtil.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                var dir = JsonUtil.Str(obj, "gameDir");
                if (string.IsNullOrWhiteSpace(dir)) return null;
                return dir;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void SaveGameDir(string gameDir)
        {
            var json = "{\r\n  \"gameDir\": \"" + Escape(gameDir) + "\"\r\n}\r\n";
            var path = FilePath;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

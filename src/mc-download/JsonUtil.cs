using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace McDownload
{
    internal static class JsonUtil
    {
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.DeserializeObject(json);
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            var obj = Parse(json) as Dictionary<string, object>;
            if (obj == null) throw new InvalidOperationException("JSON 根节点不是对象");
            return obj;
        }

        public static Dictionary<string, object> Child(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!Try(dict, key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static List<Dictionary<string, object>> ObjectList(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!Try(dict, key, out value) || value == null) return null;
            var list = value as IList;
            if (list == null) return null;
            var result = new List<Dictionary<string, object>>();
            foreach (var item in list)
            {
                var child = item as Dictionary<string, object>;
                if (child != null) result.Add(child);
            }
            return result;
        }

        public static string Str(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!Try(dict, key, out value) || value == null) return null;
            var text = value as string;
            if (text != null) return text;
            if (value is bool || value is int || value is long || value is decimal || value is double || value is float)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        public static long Long(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!Try(dict, key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0L; }
        }

        public static bool Bool(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!Try(dict, key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            var text = value as string;
            return text != null && text.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        static bool Try(Dictionary<string, object> dict, string key, out object value)
        {
            value = null;
            if (dict == null || key == null) return false;
            return dict.TryGetValue(key, out value);
        }
    }
}

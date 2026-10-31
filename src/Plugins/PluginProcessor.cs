using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件数据处理类
    /// 负责 JSON 解析、变量提取和数据转换
    /// </summary>
    public static class PluginProcessor
    {
        // [Optimization] Cached Compiled Regex for Template Resolution
        private static readonly Regex _templateRegex = new Regex(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

        /// <summary>
        /// 从 JSON 对象中提取值
        /// 支持路径语法: "data.current.temp" / "list[0].id"
        ///
        /// 扩展：数组通配 + 求和
        ///   "items[*].ip_summary.active_requests|sum"
        ///   [*] 展开数组每个元素，|sum 把命中的数字加总。
        ///   不带 |sum 时 [*] 等价于 [0]（取第一个元素），保持向后兼容。
        /// </summary>
        public static string ExtractJsonValue(JsonElement root, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return PluginConstants.STATUS_UNKNOWN;

                string p = path.Trim();

                // 1. 剥离 |sum 后缀
                bool wantSum = false;
                int pipeIdx = p.LastIndexOf('|');
                if (pipeIdx > 0 && p.Substring(pipeIdx + 1).Trim().Equals("sum", StringComparison.OrdinalIgnoreCase))
                {
                    wantSum = true;
                    p = p.Substring(0, pipeIdx).Trim();
                }

                // 2. [*] 通配：先导航到数组，再逐元素取尾路径
                int wild = p.IndexOf("[*]", StringComparison.Ordinal);
                if (wild >= 0)
                {
                    string headPath = p.Substring(0, wild);
                    string tailPath = p.Substring(wild + 3).TrimStart('.');

                    if (!TryNavigatePath(root, headPath, out var arr)) return PluginConstants.STATUS_UNKNOWN;
                    if (arr.ValueKind != JsonValueKind.Array) return PluginConstants.STATUS_UNKNOWN;

                    int count = arr.GetArrayLength();
                    if (count == 0) return PluginConstants.STATUS_UNKNOWN;

                    // 无 |sum：取首个元素，等价于 [0]
                    if (!wantSum)
                    {
                        if (!TryNavigatePath(arr[0], tailPath, out var first)) return PluginConstants.STATUS_UNKNOWN;
                        return JsonElementToString(first);
                    }

                    decimal sum = 0;
                    int counted = 0;
                    for (int i = 0; i < count; i++)
                    {
                        JsonElement v;
                        if (tailPath.Length == 0)
                        {
                            v = arr[i]; // 直接对数字数组求和
                        }
                        else if (!TryNavigatePath(arr[i], tailPath, out v))
                        {
                            continue; // 单个元素缺字段：跳过，不中断整体
                        }

                        if (v.ValueKind == JsonValueKind.Number)
                        {
                            if (v.TryGetDecimal(out decimal dn)) { sum += dn; counted++; }
                        }
                        else if (v.ValueKind == JsonValueKind.String &&
                                 decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal ds))
                        {
                            sum += ds;
                            counted++;
                        }
                    }

                    // 一个有效数字都没有：当作取不到，让上层走失败分支而不是显示 0
                    if (counted == 0) return PluginConstants.STATUS_UNKNOWN;
                    return sum.ToString(CultureInfo.InvariantCulture);
                }

                // 3. 普通路径
                if (!TryNavigatePath(root, p, out var node)) return PluginConstants.STATUS_UNKNOWN;
                return JsonElementToString(node);
            }
            catch
            {
                return PluginConstants.STATUS_UNKNOWN;
            }
        }

        /// <summary>
        /// 按 "a.b[0].c" 形式逐级导航。path 为空时直接返回起点（用于根数组）。
        /// </summary>
        private static bool TryNavigatePath(JsonElement start, string path, out JsonElement result)
        {
            result = start;
            if (string.IsNullOrWhiteSpace(path)) return true;

            foreach (var part in path.Split('.'))
            {
                if (string.IsNullOrEmpty(part)) continue;

                if (part.Contains("[") && part.EndsWith("]"))
                {
                    // Handle Array Index: list[0]
                    int idxStart = part.IndexOf('[');
                    string name = part.Substring(0, idxStart);

                    if (!int.TryParse(part.Substring(idxStart + 1, part.Length - idxStart - 2), out int index)) return false;

                    if (!string.IsNullOrEmpty(name))
                    {
                        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result)) return false;
                    }

                    if (result.ValueKind != JsonValueKind.Array || index >= result.GetArrayLength()) return false;
                    result = result[index];
                }
                else
                {
                    // Handle Object Property
                    if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(part, out result)) return false;
                }
            }
            return true;
        }

        private static string JsonElementToString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => element.GetRawText() // Object or Array as string
            };
        }

        /// <summary>
        /// 应用数据转换规则 (Transforms)
        /// </summary>
        public static void ApplyTransforms(List<PluginTransform> transforms, Dictionary<string, string> context)
        {
            if (transforms == null) return;

            foreach (var t in transforms)
            {
                string src = !string.IsNullOrEmpty(t.SourceVar) ? t.SourceVar : t.TargetVar;

                if (!context.ContainsKey(src)) continue; 

                string val = context[src];

                switch (t.Function)
                {
                    case "regex_replace":
                        val = ApplyRegexReplace(val, t, context);
                        break;
                    case "regex_match":
                        val = ApplyRegexMatch(val, t);
                        break;
                    case "map":
                        val = ApplyMap(val, t);
                        break;
                    case "resolve_template":
                        val = ResolveTemplate(val, context);
                        break;
                    case "threshold_switch":
                        val = ApplyThresholdSwitch(val, t);
                        break;
                    case "round":
                        val = ApplyRound(val, t);
                        break;
                }

                context[t.TargetVar] = val;
            }
        }

        private static string ApplyRegexReplace(string val, PluginTransform t, Dictionary<string, string> context)
        {
            try
            {
                string replacement = t.To;
                if (replacement.Contains("{{")) 
                {
                    replacement = ResolveTemplate(replacement, context);
                }
                return Regex.Replace(val, t.Pattern, replacement);
            }
            catch { return val; }
        }

        private static string ApplyRegexMatch(string val, PluginTransform t)
        {
            try
            {
                var match = Regex.Match(val, t.Pattern);
                if (match.Success)
                {
                    int groupIndex = 1;
                    if (int.TryParse(t.To, out int idx)) groupIndex = idx;
                    
                    if (groupIndex < match.Groups.Count)
                    {
                        return match.Groups[groupIndex].Value;
                    }
                }
                return "";
            }
            catch { return ""; }
        }

        private static string ApplyMap(string val, PluginTransform t)
        {
            if (t.Map != null && t.Map.ContainsKey(val))
            {
                return t.Map[val];
            }
            return val;
        }

        private static string ApplyThresholdSwitch(string val, PluginTransform t)
        {
            if (double.TryParse(val, out double numVal))
            {
                if (t.ValueMap != null && t.ValueMap.Count > 0)
                {
                    var sorted = new List<(double Th, string Val)>();
                    foreach (var kv in t.ValueMap)
                    {
                        if (double.TryParse(kv.Key, out double k)) sorted.Add((k, kv.Value));
                    }
                    sorted.Sort((a, b) => a.Th.CompareTo(b.Th));

                    string result = sorted[0].Val; 
                    
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (numVal >= sorted[i].Th) result = sorted[i].Val;
                        else break; 
                    }
                    return result;
                }
                return "0"; 
            }
            return "0";
        }

        /// <summary>
        /// 数值四舍五入并固定小数位。to = 小数位数（默认 2）。
        /// 例：346.99999999999994 → 347.00；347 → 347.00；347.005 → 347.01
        /// </summary>
        private static string ApplyRound(string val, PluginTransform t)
        {
            int digits = 2;
            if (!string.IsNullOrEmpty(t.To) && int.TryParse(t.To, out int d)) digits = d;
            if (digits < 0) digits = 0;
            if (digits > 10) digits = 10;

            if (decimal.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec))
            {
                dec = Math.Round(dec, digits, MidpointRounding.AwayFromZero);
                return dec.ToString("F" + digits, CultureInfo.InvariantCulture);
            }
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbl))
            {
                dbl = Math.Round(dbl, digits, MidpointRounding.AwayFromZero);
                return dbl.ToString("F" + digits, CultureInfo.InvariantCulture);
            }
            return val;
        }

        /// <summary>
        /// 处理字符串模版替换
        /// </summary>
        public static string ResolveTemplate(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (!template.Contains("{{")) return template;

            return _templateRegex.Replace(template, m =>
            {
                string content = m.Groups[1].Value.Trim();
                
                // Handle Ternary Syntax: "cond ? trueVal : falseVal"
                int qIdx = content.IndexOf('?');
                int cIdx = content.LastIndexOf(':');
                if (qIdx > 0 && cIdx > qIdx && (qIdx + 1 >= content.Length || content[qIdx + 1] != '?'))
                {
                    string condKey = content.Substring(0, qIdx).Trim();
                    string truePart = content.Substring(qIdx + 1, cIdx - (qIdx + 1)).Trim();
                    string falsePart = content.Substring(cIdx + 1).Trim();

                    bool isTrue = context.TryGetValue(condKey, out string val) && !string.IsNullOrEmpty(val);
                    string target = isTrue ? truePart : falsePart;

                    // Remove quotes if present
                    if (target.Length >= 2 && ((target.StartsWith("\"") && target.EndsWith("\"")) || (target.StartsWith("'") && target.EndsWith("'"))))
                    {
                        return target.Substring(1, target.Length - 2);
                    }

                    // Treat as variable if not quoted
                    return context.TryGetValue(target, out string tVal) ? tVal : "";
                }

                // Handle Fallback Syntax: "var ?? fallback"
                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();

                        // Check for string literal
                        if (key.Length >= 2 && ((key.StartsWith("\"") && key.EndsWith("\"")) || (key.StartsWith("'") && key.EndsWith("'"))))
                        {
                            return key.Substring(1, key.Length - 2);
                        }

                        if (context.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            return val;
                        }
                    }
                    return ""; // All fallbacks failed
                }
                
                // Standard Lookup
                if (context.TryGetValue(content, out string value))
                {
                    return value;
                }

                // 内置时间变量：每次求值现算，保证跨过 0 点后日期会翻
                if (TryResolveBuiltin(content, out string builtin))
                {
                    return builtin;
                }

                return "";
            });
        }

        /// <summary>
        /// 内置时间变量（插件模板可直接引用，无需在 inputs 里声明）：
        /// __today__        本地日期 yyyy-MM-dd
        /// __today_start__  本地当天 0 点，换算成 UTC 的 ISO 时间（yyyy-MM-ddTHH:mm:ssZ）
        /// __now__          本地时间 yyyy-MM-ddTHH:mm:ss
        /// __now_ms__       本地时间 yyyy-MM-ddTHH:mm:ss.fff
        /// __unix__         当前 Unix 时间戳（秒）
        /// __utc__          UTC 时间 yyyy-MM-ddTHH:mm:ssZ
        /// </summary>
        private static bool TryResolveBuiltin(string name, out string value)
        {
            DateTime now = DateTime.Now;
            switch (name)
            {
                case "__today__":
                    value = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                case "__today_start__":
                    // 本地当天 0 点 → UTC。部分服务端会把纯日期当成 UTC 0 点，
                    // 直接用纯日期会让东八区少算 8 小时（0:00~8:00 的数据全丢）。
                    DateTime localMidnight = now.Date;
                    value = localMidnight.ToUniversalTime()
                        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                    return true;
                case "__now__":
                    value = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                    return true;
                case "__now_ms__":
                    value = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    return true;
                case "__unix__":
                    value = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    return true;
                case "__utc__":
                    value = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                    return true;
                default:
                    value = "";
                    return false;
            }
        }
    }
}

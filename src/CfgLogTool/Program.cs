using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CfgLogTool;

/// <summary>
/// 仪器工作站 .cfg 系统日志工具：解码 / 查看 / 删除 / 修改时间 / 重编码。
/// 容器格式: 4 字节小端 u32(解压后长度) + zlib 流(78 9C)。
/// 载荷格式: UTF-8 Delphi VCL 伪 XML（属性写在 > 之后，非标准 XML，须正则解析）。
/// </summary>
public static class Program
{
    private static readonly Regex EntryRe = new(@"<TLogData>(.*?)</TLogData>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrRe = new(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex FileAttrRe = new(@"<TLogFile>(.*?)<ScanData>", RegexOptions.Singleline | RegexOptions.Compiled);

    private sealed class Entry
    {
        public required Dictionary<string, string> Attrs { get; init; }
        public string Get(string key) => Attrs.TryGetValue(key, out var v) ? v : "";
    }

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略 */ }
        try
        {
            if (args.Length == 0) return Help(0);
            return args[0].ToLowerInvariant() switch
            {
                "list"    => CmdList(args),
                "decode"  => CmdDecode(args),
                "remove"  => CmdRemove(args),
                "setdate" => CmdSetDate(args),
                "help" or "--help" or "-h" => Help(0),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    // ---------- 核心: 解码 / 编码 ----------

    private static (string Text, uint Declared) DecodeFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 5) throw new InvalidDataException("文件过小，不是有效的 .cfg 日志容器");
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        using var ms = new MemoryStream(bytes, 4, bytes.Length - 4, writable: false);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        using var outs = new MemoryStream();
        zs.CopyTo(outs);
        var data = outs.ToArray();
        if (declared != data.Length)
            Console.Error.WriteLine($"警告: 头部长度 {declared} 与实际解压长度 {data.Length} 不一致，仍继续处理");
        var text = Encoding.UTF8.GetString(data);
        if (!text.StartsWith("<TLogFiles", StringComparison.Ordinal))
            throw new InvalidDataException("解压后不是 <TLogFiles> 结构，可能不是本格式的日志文件");
        return (text, declared);
    }

    private static byte[] EncodeText(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)raw.Length);
        ms.Write(header);
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw);
        return ms.ToArray();
    }

    private static List<Entry> ParseEntries(string text)
    {
        var list = new List<Entry>();
        foreach (Match m in EntryRe.Matches(text))
            list.Add(new Entry { Attrs = ParseAttrs(m.Groups[1].Value) });
        return list;
    }

    private static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match a in AttrRe.Matches(s)) d[a.Groups[1].Value] = a.Groups[2].Value;
        return d;
    }

    private static Dictionary<string, string> ParseFileAttrs(string text)
    {
        var m = FileAttrRe.Match(text);
        return m.Success ? ParseAttrs(m.Groups[1].Value) : [];
    }

    /// <summary>按 ItemDate 前缀删除条目；其余文本按字节原样拼接，不重建 XML。</summary>
    private static (string NewText, List<Entry> Removed) RemoveByTimePrefix(string text, string prefix)
    {
        var removed = new List<Entry>();
        var sb = new StringBuilder(text.Length);
        int last = 0;
        foreach (Match m in EntryRe.Matches(text))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            if (attrs.TryGetValue("ItemDate", out var d) && d.StartsWith(prefix, StringComparison.Ordinal))
            {
                sb.Append(text.AsSpan(last, m.Index - last));
                last = m.Index + m.Length;
                removed.Add(new Entry { Attrs = attrs });
            }
        }
        sb.Append(text.AsSpan(last));
        return (sb.ToString(), removed);
    }

    /// <summary>按 ItemObj 修改某条记录的 ItemDate；其余文本字节原样保留。</summary>
    private static (string NewText, Entry Changed, string OldDate, string NewDate) SetDateById(string text, string id, string newDate)
    {
        var hits = new List<(Match M, Dictionary<string, string> Attrs)>();
        foreach (Match m in EntryRe.Matches(text))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            if (attrs.TryGetValue("ItemObj", out var obj) && obj.Equals(id, StringComparison.OrdinalIgnoreCase))
                hits.Add((m, attrs));
        }
        if (hits.Count == 0)
            throw new InvalidDataException($"未找到 ItemObj 为 {id} 的记录");
        if (hits.Count > 1)
            throw new InvalidDataException($"ItemObj {id} 匹配到 {hits.Count} 条记录（应当唯一），已中止。可用 decode 输出后核对");

        var (m1, attrs1) = hits[0];
        string oldDate = attrs1.GetValueOrDefault("ItemDate", "");
        string oldSeg = $"ItemDate=\"{oldDate}\"", newSeg = $"ItemDate=\"{newDate}\"";
        int pos = m1.Value.IndexOf(oldSeg, StringComparison.Ordinal);
        if (pos < 0) throw new InvalidOperationException("内部错误: 在条目内未定位到 ItemDate 属性");
        string newEntry = m1.Value[..pos] + newSeg + m1.Value[(pos + oldSeg.Length)..];

        var sb = new StringBuilder(text.Length);
        sb.Append(text.AsSpan(0, m1.Index));
        sb.Append(newEntry);
        sb.Append(text.AsSpan(m1.Index + m1.Length));
        return (sb.ToString(), new Entry { Attrs = attrs1 }, oldDate, newDate);
    }

    // ---------- 命令 ----------

    private static int CmdList(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("用法: list <file.cfg>"); return 2; }
        var (text, _) = DecodeFile(args[1]);
        var fa = ParseFileAttrs(text);
        Console.WriteLine($"日志编号 : {fa.GetValueOrDefault("ItemCode", "?")}");
        Console.WriteLine($"日志类型 : {fa.GetValueOrDefault("ItemType", "?")}");
        Console.WriteLine($"创建时间 : {fa.GetValueOrDefault("Creationtime", "?")}");
        var entries = ParseEntries(text);
        Console.WriteLine($"条目总数 : {entries.Count}");
        Console.WriteLine(new string('-', 72));
        int i = 1;
        foreach (var e in entries)
            Console.WriteLine($"{i++,3}. [{e.Get("ItemDate")}] {e.Get("ItemName")} - {e.Get("ItemType")} - {e.Get("ItemMark")}  {{{e.Get("ItemObj").Trim('{', '}')}}}");
        return 0;
    }

    private static int CmdDecode(string[] args)
    {
        string input = "", outDir = "";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (input == "" && !args[i].StartsWith('-')) input = args[i];
        }
        if (input == "") { Console.Error.WriteLine("用法: decode <file.cfg> [--out <目录>]"); return 2; }
        var (text, _) = DecodeFile(input);
        var fa = ParseFileAttrs(text);
        var entries = ParseEntries(text);
        string baseName = Path.GetFileNameWithoutExtension(input);
        outDir = outDir == "" ? Path.GetDirectoryName(Path.GetFullPath(input))! : Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        // 1) 标准 XML（属性移入尖括号内）
        string xml = text.Replace("<TLogFile> ", "<TLogFile ").Replace("<TLogData> ", "<TLogData ");
        File.WriteAllText(Path.Combine(outDir, baseName + ".xml"), xml, new UTF8Encoding(false));

        // 2) CSV (utf-8 BOM, Excel 直开)
        var sb = new StringBuilder();
        sb.AppendLine("ItemDate,ItemName,ItemType,ItemMark,ItemCode,ItemObj");
        foreach (var e in entries)
            sb.AppendLine(string.Join(",",
                Csv(e.Get("ItemDate")), Csv(e.Get("ItemName")), Csv(e.Get("ItemType")),
                Csv(e.Get("ItemMark")), Csv(e.Get("ItemCode")), Csv(e.Get("ItemObj"))));
        File.WriteAllText(Path.Combine(outDir, baseName + ".csv"), sb.ToString(), new UTF8Encoding(true));

        // 3) 可读报告
        var rpt = new StringBuilder();
        rpt.AppendLine($"软件系统日志解码报告 - {Path.GetFileName(input)}");
        rpt.AppendLine(new string('=', 60));
        rpt.AppendLine();
        rpt.AppendLine($"日志编号: {fa.GetValueOrDefault("ItemCode", "?")} ({fa.GetValueOrDefault("ItemType", "?")})");
        rpt.AppendLine($"创建时间: {fa.GetValueOrDefault("Creationtime", "?")}");
        rpt.AppendLine($"条目总数: {entries.Count}");
        rpt.AppendLine();
        rpt.AppendLine(new string('-', 60));
        int n = 1;
        foreach (var e in entries)
            rpt.AppendLine($"{n++,3}. [{e.Get("ItemDate")}] {e.Get("ItemName")} - {e.Get("ItemType")} - {e.Get("ItemMark")}");
        File.WriteAllText(Path.Combine(outDir, baseName + "_readable.txt"), rpt.ToString(), new UTF8Encoding(false));

        Console.WriteLine($"已解码 {entries.Count} 条，输出到: {outDir}");
        Console.WriteLine($"  {baseName}.xml / {baseName}.csv / {baseName}_readable.txt");
        return 0;
    }

    private static int CmdRemove(string[] args)
    {
        string input = "", timePrefix = "", output = "";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--time" && i + 1 < args.Length) timePrefix = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else if (input == "" && !args[i].StartsWith('-')) input = args[i];
        }
        if (input == "" || timePrefix == "")
        {
            Console.Error.WriteLine("用法: remove <file.cfg> --time <时间前缀> [--out <新文件.cfg>]");
            Console.Error.WriteLine("示例: remove sample.cfg --time \"2024-05-20 08:31\" [--out sample_new.cfg]");
            return 2;
        }

        var (text, _) = DecodeFile(input);
        var before = ParseEntries(text).Count;
        var (newText, removed) = RemoveByTimePrefix(text, timePrefix);

        if (removed.Count == 0)
        {
            Console.WriteLine($"没有匹配时间前缀 \"{timePrefix}\" 的条目，文件未修改。");
            return 0;
        }
        int after = before - removed.Count;
        if (EntryRe.Matches(newText).Count != after)
            throw new InvalidOperationException("内部校验失败: 剩余条目数不一致");

        output = output == ""
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_new.cfg")
            : Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException($"输出文件已存在: {output}（请先删除或用 --out 指定其他路径）");

        File.WriteAllBytes(output, EncodeText(newText));

        // 回读验证
        var (backText, _) = DecodeFile(output);
        if (backText != newText) throw new InvalidOperationException("回读验证失败: 重编码文件解压后与编辑内容不一致");

        Console.WriteLine($"已删除 {removed.Count} 条, 保留 {after} 条 (原 {before} 条):");
        foreach (var e in removed)
            Console.WriteLine($"  删除 [{e.Get("ItemDate")}] {e.Get("ItemName")} - {e.Get("ItemType")} - {e.Get("ItemMark")}");
        Console.WriteLine($"新文件: {output} ({new FileInfo(output).Length} 字节, 解压 {Encoding.UTF8.GetByteCount(newText)} 字节)");
        Console.WriteLine("回读验证: 通过");
        return 0;
    }

    private static int CmdSetDate(string[] args)
    {
        string input = "", id = "", dateStr = "", output = "";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--id" && i + 1 < args.Length) id = args[++i];
            else if (args[i] == "--date" && i + 1 < args.Length) dateStr = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else if (input == "" && !args[i].StartsWith('-')) input = args[i];
        }
        if (input == "" || id == "" || dateStr == "")
        {
            Console.Error.WriteLine("用法: setdate <file.cfg> --id <ItemObj GUID> --date <时间> [--out <新文件.cfg>]");
            Console.Error.WriteLine("示例: setdate sample.cfg --id \"{AAAAAAAA-BBBB-CCCC-DDDD-000000000001}\" --date \"2024-05-20 08:40:00\"");
            Console.Error.WriteLine("提示: 各条目的 ItemObj 可用 list 命令查看");
            return 2;
        }
        if (!id.StartsWith('{') || !id.EndsWith('}'))
            id = "{" + id.Trim() + "}";

        // 时间格式校验并规范化
        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/M/d H:m:s", "yyyy/M/d H:m"];
        if (!DateTime.TryParseExact(dateStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new FormatException($"时间格式无效: \"{dateStr}\"，应为 yyyy-MM-dd HH:mm:ss");
        string newDate = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var (text, _) = DecodeFile(input);
        var (newText, changed, oldDate, _) = SetDateById(text, id, newDate);
        if (oldDate == newDate)
        {
            Console.WriteLine($"条目 {id} 的 ItemDate 已经是 {newDate}，无需修改。");
            return 0;
        }

        output = output == ""
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_edit.cfg")
            : Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException($"输出文件已存在: {output}（请先删除或用 --out 指定其他路径）");

        File.WriteAllBytes(output, EncodeText(newText));

        // 回读验证: 解压一致 + 该条目时间已更新
        var (backText, _) = DecodeFile(output);
        if (backText != newText) throw new InvalidOperationException("回读验证失败: 重编码文件解压后与编辑内容不一致");
        var after = ParseEntries(backText).First(e =>
            e.Attrs.TryGetValue("ItemObj", out var o) && o.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (after.Get("ItemDate") != newDate) throw new InvalidOperationException("回读验证失败: 目标条目时间未更新");

        Console.WriteLine("已修改 1 条记录的 ItemDate:");
        Console.WriteLine($"  条目   : {id}");
        Console.WriteLine($"  用户   : {changed.Get("ItemName")} - {changed.Get("ItemType")} - {changed.Get("ItemMark")}");
        Console.WriteLine($"  旧时间 : {oldDate}");
        Console.WriteLine($"  新时间 : {newDate}");
        Console.WriteLine($"新文件: {output} ({new FileInfo(output).Length} 字节, 解压 {Encoding.UTF8.GetByteCount(newText)} 字节)");
        Console.WriteLine("回读验证: 通过");
        return 0;
    }

    private static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static int Unknown(string cmd) { Console.Error.WriteLine($"未知命令: {cmd}"); return Help(2); }

    private static int Help(int code)
    {
        Console.WriteLine("""
        CfgLogTool — 仪器工作站 .cfg 系统日志 解码/编辑/重编码工具 (.NET 10)

        格式: 4 字节小端 u32 (解压后长度) + zlib 流; 载荷为 Delphi VCL 伪 XML。

        用法:
          list    <file.cfg>                        查看日志信息与全部条目(含各条 ItemObj)
          decode  <file.cfg> [--out <目录>]         解码输出 .xml/.csv/_readable.txt 三件套
          remove  <file.cfg> --time <时间前缀>      删除匹配时间前缀的条目并重编码
                  [--out <新文件.cfg>]
          setdate <file.cfg> --id <ItemObj GUID>    修改指定条目的 ItemDate 并重编码
                  --date <时间> [--out <新文件.cfg>]

        示例:
          CfgLogTool list sample.cfg
          CfgLogTool decode sample.cfg --out ./out
          CfgLogTool remove sample.cfg --time "2024-05-20 08:31"
          CfgLogTool setdate sample.cfg --id "{AAAAAAAA-BBBB-CCCC-DDDD-000000000001}" --date "2024-05-20 08:40:00"

        说明:
          --time 为 ItemDate 前缀匹配: 精确到秒/分/时/日均可, 如 "2024-05-20 08:31"。
          --id 按 ItemObj GUID 精确定位(忽略大小写, 带不带大括号均可), 须唯一命中。
          --date 支持 yyyy-MM-dd HH:mm:ss / yyyy-MM-dd HH:mm 等格式, 自动规范化。
          remove/setdate 均不覆盖原文件, 默认输出 <原名>_new.cfg / <原名>_edit.cfg;
          写盘前做回读验证, 保证除目标改动外其余字节不变。
        """);
        return code;
    }
}

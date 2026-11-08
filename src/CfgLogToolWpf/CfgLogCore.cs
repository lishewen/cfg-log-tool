using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CfgLogToolWpf;

/// <summary>
/// .cfg 日志编解码内核: 4字节小端u32长度 + zlib流; 载荷为 Delphi VCL 伪 XML。
/// 所有修改均以"原文切片替换"方式实现, 保证除目标改动外字节不变。
/// </summary>
public static class CfgLogCore
{
    public static readonly Regex EntryRe = new(@"<TLogData>(.*?)</TLogData>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrRe = new(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex FileAttrRe = new(@"<TLogFile>(.*?)<ScanData>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match a in AttrRe.Matches(s)) d[a.Groups[1].Value] = a.Groups[2].Value;
        return d;
    }

    public static List<Dictionary<string, string>> ParseEntries(string text)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (Match m in EntryRe.Matches(text))
            list.Add(ParseAttrs(m.Groups[1].Value));
        return list;
    }

    public static Dictionary<string, string> ParseFileAttrs(string text)
    {
        var m = FileAttrRe.Match(text);
        return m.Success ? ParseAttrs(m.Groups[1].Value) : [];
    }

    public static string DecodeFile(string path)
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
            throw new InvalidDataException($"头部长度 {declared} 与实际解压长度 {data.Length} 不一致，文件可能损坏");
        var text = Encoding.UTF8.GetString(data);
        if (!text.StartsWith("<TLogFiles", StringComparison.Ordinal))
            throw new InvalidDataException("解压后不是 <TLogFiles> 结构，可能不是本格式的日志文件");
        return text;
    }

    public static byte[] EncodeText(string text)
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

    /// <summary>按 ItemObj 集合删除条目。</summary>
    public static (string Text, int Removed) RemoveByItemObjs(string text, HashSet<string> objs)
    {
        if (objs.Count == 0) return (text, 0);
        var sb = new StringBuilder(text.Length);
        int last = 0, removed = 0;
        foreach (Match m in EntryRe.Matches(text))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            if (attrs.TryGetValue("ItemObj", out var obj) && objs.Contains(obj))
            {
                sb.Append(text.AsSpan(last, m.Index - last));
                last = m.Index + m.Length;
                removed++;
            }
        }
        sb.Append(text.AsSpan(last));
        return (sb.ToString(), removed);
    }

    /// <summary>按 ItemObj 修改单条 ItemDate。</summary>
    public static string SetDateByObj(string text, string id, string newDate)
    {
        Match? hit = null;
        Dictionary<string, string>? hitAttrs = null;
        int dup = 0;
        foreach (Match m in EntryRe.Matches(text))
        {
            var attrs = ParseAttrs(m.Groups[1].Value);
            if (attrs.TryGetValue("ItemObj", out var obj) && obj.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                if (hit is not null) dup++;
                else { hit = m; hitAttrs = attrs; }
            }
        }
        if (hit is null) throw new InvalidDataException($"未找到 ItemObj 为 {id} 的记录");
        if (dup > 0) throw new InvalidDataException($"ItemObj {id} 匹配到 {dup + 1} 条记录（应当唯一），已中止");

        string oldDate = hitAttrs!.GetValueOrDefault("ItemDate", "");
        string oldSeg = $"ItemDate=\"{oldDate}\"", newSeg = $"ItemDate=\"{newDate}\"";
        int pos = hit.Value.IndexOf(oldSeg, StringComparison.Ordinal);
        if (pos < 0) throw new InvalidOperationException("内部错误: 在条目内未定位到 ItemDate 属性");
        string newEntry = hit.Value[..pos] + newSeg + hit.Value[(pos + oldSeg.Length)..];

        var sb = new StringBuilder(text.Length);
        sb.Append(text.AsSpan(0, hit.Index));
        sb.Append(newEntry);
        sb.Append(text.AsSpan(hit.Index + hit.Length));
        return sb.ToString();
    }

    // ---------- 导出三件套 ----------

    public static string BuildFixedXml(string text) =>
        text.Replace("<TLogFile> ", "<TLogFile ").Replace("<TLogData> ", "<TLogData ");

    public static string BuildCsv(List<Dictionary<string, string>> entries)
    {
        static string Csv(string s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new StringBuilder();
        sb.AppendLine("ItemDate,ItemName,ItemType,ItemMark,ItemCode,ItemObj");
        foreach (var e in entries)
            sb.AppendLine(string.Join(",",
                Csv(e.GetValueOrDefault("ItemDate")), Csv(e.GetValueOrDefault("ItemName")),
                Csv(e.GetValueOrDefault("ItemType")), Csv(e.GetValueOrDefault("ItemMark")),
                Csv(e.GetValueOrDefault("ItemCode")), Csv(e.GetValueOrDefault("ItemObj"))));
        return sb.ToString();
    }

    public static string BuildReadable(string fileName, Dictionary<string, string> fileAttrs, List<Dictionary<string, string>> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"软件系统日志报告 - {fileName}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();
        sb.AppendLine($"日志编号: {fileAttrs.GetValueOrDefault("ItemCode", "?")} ({fileAttrs.GetValueOrDefault("ItemType", "?")})");
        sb.AppendLine($"创建时间: {fileAttrs.GetValueOrDefault("Creationtime", "?")}");
        sb.AppendLine($"条目总数: {entries.Count}");
        sb.AppendLine();
        sb.AppendLine(new string('-', 60));
        int n = 1;
        foreach (var e in entries)
            sb.AppendLine($"{n++,3}. [{e.GetValueOrDefault("ItemDate")}] {e.GetValueOrDefault("ItemName")} - {e.GetValueOrDefault("ItemType")} - {e.GetValueOrDefault("ItemMark")}");
        return sb.ToString();
    }
}

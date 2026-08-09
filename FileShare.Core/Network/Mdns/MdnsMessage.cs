using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace FileShare.Core.Network.Mdns;

/// <summary>
/// mDNS 最小 DNS 报文编解码器。
/// <para>仅支持 mDNS 发现所需的最小记录类型集合：PTR（服务指针）、SRV（服务位置）、TXT（文本属性）、A（IPv4 主机地址）。
/// 编码不做名字压缩（逐标签写出），解码支持名字压缩指针（0xC0 前缀），保证与其它 mDNS 实现互操作。</para>
/// <para>纯字节操作，无反射，AOT 安全。</para>
/// </summary>
internal static class MdnsCodec
{
    public const ushort TypeA = 1;       // IPv4 主机地址
    public const ushort TypePTR = 12;    // 域名指针（服务实例 → 服务类型）
    public const ushort TypeTXT = 16;    // 文本属性
    public const ushort TypeSRV = 33;    // 服务位置（优先级/权重/端口/目标）
    public const ushort ClassIN = 1;     // Internet 类
    public const ushort ClassCacheFlush = 0x8001; // mDNS 缓存刷新标志（IN | 0x8000）

    /// <summary>编码报文为字节数组。</summary>
    public static byte[] Encode(MdnsMessage message)
    {
        using var ms = new MemoryStream(512);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);

        w.Write(ToBigEndian(message.Id));
        w.Write(ToBigEndian(message.Flags));
        w.Write(ToBigEndian((ushort)message.Questions.Count));
        w.Write(ToBigEndian((ushort)message.Answers.Count));
        w.Write(ToBigEndian((ushort)0)); // NSCOUNT
        w.Write(ToBigEndian((ushort)0)); // ARCOUNT

        foreach (var q in message.Questions)
        {
            WriteName(w, q.Name);
            w.Write(ToBigEndian(q.Type));
            w.Write(ToBigEndian(q.Class));
        }

        foreach (var r in message.Answers)
        {
            WriteName(w, r.Name);
            w.Write(ToBigEndian(r.Type));
            w.Write(ToBigEndian(r.Class));
            w.Write(ToBigEndian((uint)r.Ttl));
            w.Write(ToBigEndian((ushort)r.Rdata.Length));
            w.Write(r.Rdata);
        }

        return ms.ToArray();
    }

    /// <summary>解码字节数组为报文。解析失败返回 null（mDNS 容错：忽略畸形包）。</summary>
    public static MdnsMessage? Decode(byte[] data, int length)
    {
        if (length < 12) return null;
        try
        {
            using var ms = new MemoryStream(data, 0, length);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

            var id = ReadUInt16BigEndian(r);
            var flags = ReadUInt16BigEndian(r);
            var qd = ReadUInt16BigEndian(r);
            var an = ReadUInt16BigEndian(r);
            var ns = ReadUInt16BigEndian(r);
            var ar = ReadUInt16BigEndian(r);

            var msg = new MdnsMessage { Id = id, Flags = flags };

            for (int i = 0; i < qd; i++)
            {
                var name = ReadName(r);
                var type = ReadUInt16BigEndian(r);
                var cls = ReadUInt16BigEndian(r);
                msg.Questions.Add(new MdnsQuestion { Name = name, Type = type, Class = cls });
            }

            for (int i = 0; i < an; i++)
            {
                var rr = ReadRecord(r);
                if (rr != null) msg.Answers.Add(rr);
            }

            // 跳过 Authority 与 Additional 段（本实现不消费，但仍需推进读位置以避免后续解析错位）
            for (int i = 0; i < ns + ar; i++)
            {
                var rr = ReadRecord(r);
                if (rr != null && i >= qd + an) { /* 丢弃，仅消费字节 */ }
            }

            return msg;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析 SRV 记录 RDATA：优先级(2) + 权重(2) + 端口(2) + 目标域名。</summary>
    public static (int Priority, int Weight, int Port, string Target) ParseSrv(byte[] rdata)
    {
        using var ms = new MemoryStream(rdata);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        var priority = ReadUInt16BigEndian(r);
        var weight = ReadUInt16BigEndian(r);
        var port = ReadUInt16BigEndian(r);
        var target = ReadName(r);
        return (priority, weight, port, target);
    }

    /// <summary>解析 TXT 记录 RDATA 为键值字典（每个长度前缀字符串形如 key=value）。</summary>
    public static Dictionary<string, string> ParseTxt(byte[] rdata)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        while (pos < rdata.Length)
        {
            var len = rdata[pos++];
            if (len == 0 || pos + len > rdata.Length) break;
            var entry = Encoding.UTF8.GetString(rdata, pos, len);
            pos += len;
            var eq = entry.IndexOf('=');
            if (eq > 0)
            {
                var key = entry[..eq];
                var val = entry[(eq + 1)..];
                dict[key] = val;
            }
        }
        return dict;
    }

    /// <summary>解析 A 记录 RDATA 为 IPv4。</summary>
    public static IPAddress ParseA(byte[] rdata)
    {
        return rdata.Length == 4 ? new IPAddress(rdata) : IPAddress.None;
    }

    /// <summary>
    /// 解析 PTR 记录 RDATA 为目标域名。
    /// <para>注意：仅支持未压缩的完整域名（本实现编码不压缩）。对端若使用压缩指针，此处返回部分名，
    /// 但公告通常同时携带 SRV/TXT（其 NAME 字段即实例名），故此处为尽力解析。</para>
    /// </summary>
    public static string ParsePtr(byte[] rdata)
    {
        if (rdata.Length == 0) return string.Empty;
        using var ms = new MemoryStream(rdata);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
        return ReadName(r);
    }

    /// <summary>构造 TXT 记录 RDATA（键值对序列化为长度前缀字符串）。</summary>
    public static byte[] BuildTxt(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        foreach (var kv in entries)
        {
            var entry = $"{kv.Key}={kv.Value}";
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255) continue; // 单 TXT 字符串上限 255
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>构造 SRV 记录 RDATA。</summary>
    public static byte[] BuildSrv(int priority, int weight, int port, string target)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
        w.Write(ToBigEndian((ushort)priority));
        w.Write(ToBigEndian((ushort)weight));
        w.Write(ToBigEndian((ushort)port));
        WriteName(w, target);
        return ms.ToArray();
    }

    /// <summary>构造 PTR 记录 RDATA（指向的域名）。</summary>
    public static byte[] BuildPtr(string target)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
        WriteName(w, target);
        return ms.ToArray();
    }

    /// <summary>构造 A 记录 RDATA。</summary>
    public static byte[] BuildA(IPAddress address)
    {
        return address.GetAddressBytes();
    }

    #region 名称读写

    /// <summary>写出域名（逐标签，不做压缩）。空名写出单字节 0。</summary>
    private static void WriteName(BinaryWriter w, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            w.Write((byte)0);
            return;
        }
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length == 0 || bytes.Length > 63) continue; // 标签上限 63
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }
        w.Write((byte)0); // 终止符
    }

    /// <summary>读取域名，支持压缩指针（0xC0 前缀）。返回完整点分域名。</summary>
    private static string ReadName(BinaryReader r)
    {
        var labels = new List<string>();
        var posAfter = -1; // 跟踪指针后应返回的读取位置
        var jumped = false;
        var guard = 0; // 防止指针环

        while (true)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) break;
            var len = r.ReadByte();
            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                // 压缩指针：2 字节，高 2 位为 11，低 14 位为偏移
                var b2 = r.ReadByte();
                if (!jumped) posAfter = (int)r.BaseStream.Position;
                jumped = true;
                var offset = ((len & 0x3F) << 8) | b2;
                r.BaseStream.Position = offset;
                if (++guard > 128) break; // 防环
                continue;
            }

            var labelBytes = r.ReadBytes(len);
            if (labelBytes.Length < len) break;
            labels.Add(Encoding.UTF8.GetString(labelBytes));
        }

        if (jumped && posAfter >= 0) r.BaseStream.Position = posAfter;
        return string.Join('.', labels);
    }

    #endregion

    private static MdnsRecord? ReadRecord(BinaryReader r)
    {
        var name = ReadName(r);
        var type = ReadUInt16BigEndian(r);
        var cls = ReadUInt16BigEndian(r);
        var ttl = ReadUInt32BigEndian(r);
        var rdlen = ReadUInt16BigEndian(r);
        var rdata = r.ReadBytes(rdlen);
        if (rdata.Length < rdlen) return null;
        return new MdnsRecord { Name = name, Type = type, Class = cls, Ttl = ttl, Rdata = rdata };
    }

    private static ushort ReadUInt16BigEndian(BinaryReader r)
    {
        var b = r.ReadBytes(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadUInt32BigEndian(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static byte[] ToBigEndian(ushort v) => new byte[] { (byte)(v >> 8), (byte)(v & 0xFF) };
    private static byte[] ToBigEndian(uint v) => new byte[]
    {
        (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
        (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)
    };
}

/// <summary>mDNS 报文。</summary>
internal sealed class MdnsMessage
{
    public ushort Id { get; set; }
    public ushort Flags { get; set; }
    public List<MdnsQuestion> Questions { get; } = new();
    public List<MdnsRecord> Answers { get; } = new();

    /// <summary>是否为响应（QR 位）。</summary>
    public bool IsResponse => (Flags & 0x8000) != 0;
}

/// <summary>mDNS 查询问题。</summary>
internal sealed class MdnsQuestion
{
    public string Name { get; set; } = string.Empty;
    public ushort Type { get; set; }
    public ushort Class { get; set; }
}

/// <summary>mDNS 资源记录。</summary>
internal sealed class MdnsRecord
{
    public string Name { get; set; } = string.Empty;
    public ushort Type { get; set; }
    public ushort Class { get; set; }
    public uint Ttl { get; set; }
    public byte[] Rdata { get; set; } = Array.Empty<byte>();
}

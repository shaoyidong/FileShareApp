using System.Net;
using FileShare.Core.Network.Mdns;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// mDNS 报文编解码器单元测试：验证 PTR/SRV/TXT/A 记录的编码-解码往返一致性。
/// </summary>
public class MdnsCodecTests
{
    private const string ServiceType = "_fileshare._tcp.local";

    [Fact]
    public void EncodeDecode_AnnouncementWithAllRecordTypes_RoundTrips()
    {
        // Arrange：构造一个完整的服务公告报文（PTR + SRV + TXT + A）
        var instance = "device-abc._fileshare._tcp.local";
        var host = "device-abc.local";
        var ip = IPAddress.Parse("192.168.1.42");

        var msg = new MdnsMessage
        {
            Id = 0,
            Flags = 0x8400
        };
        msg.Answers.Add(new MdnsRecord
        {
            Name = ServiceType,
            Type = MdnsCodec.TypePTR,
            Class = MdnsCodec.ClassIN,
            Ttl = 4500,
            Rdata = MdnsCodec.BuildPtr(instance)
        });
        msg.Answers.Add(new MdnsRecord
        {
            Name = instance,
            Type = MdnsCodec.TypeSRV,
            Class = MdnsCodec.ClassCacheFlush,
            Ttl = 4500,
            Rdata = MdnsCodec.BuildSrv(0, 0, 5237, host)
        });
        msg.Answers.Add(new MdnsRecord
        {
            Name = instance,
            Type = MdnsCodec.TypeTXT,
            Class = MdnsCodec.ClassCacheFlush,
            Ttl = 4500,
            Rdata = MdnsCodec.BuildTxt(new Dictionary<string, string>
            {
                ["id"] = "device-abc",
                ["name"] = "My Device",
                ["type"] = "Desktop",
                ["tls"] = "1"
            })
        });
        msg.Answers.Add(new MdnsRecord
        {
            Name = host,
            Type = MdnsCodec.TypeA,
            Class = MdnsCodec.ClassCacheFlush,
            Ttl = 4500,
            Rdata = MdnsCodec.BuildA(ip)
        });

        // Act
        var bytes = MdnsCodec.Encode(msg);
        var decoded = MdnsCodec.Decode(bytes, bytes.Length);

        // Assert
        Assert.NotNull(decoded);
        Assert.True(decoded!.IsResponse);
        Assert.Equal(4, decoded.Answers.Count);

        // PTR
        var ptr = decoded.Answers[0];
        Assert.Equal(MdnsCodec.TypePTR, ptr.Type);
        Assert.Equal(ServiceType, ptr.Name);
        Assert.Equal(instance, MdnsCodec.ParsePtr(ptr.Rdata));

        // SRV
        var srv = decoded.Answers[1];
        Assert.Equal(MdnsCodec.TypeSRV, srv.Type);
        Assert.Equal(instance, srv.Name);
        var (prio, weight, port, target) = MdnsCodec.ParseSrv(srv.Rdata);
        Assert.Equal(5237, port);
        Assert.Equal(host, target);
        Assert.Equal(0, prio);
        Assert.Equal(0, weight);

        // TXT
        var txt = decoded.Answers[2];
        Assert.Equal(MdnsCodec.TypeTXT, txt.Type);
        var txtDict = MdnsCodec.ParseTxt(txt.Rdata);
        Assert.Equal("device-abc", txtDict["id"]);
        Assert.Equal("My Device", txtDict["name"]);
        Assert.Equal("Desktop", txtDict["type"]);
        Assert.Equal("1", txtDict["tls"]);

        // A
        var a = decoded.Answers[3];
        Assert.Equal(MdnsCodec.TypeA, a.Type);
        Assert.Equal(host, a.Name);
        Assert.Equal(ip, MdnsCodec.ParseA(a.Rdata));
    }

    [Fact]
    public void EncodeDecode_Query_RoundTrips()
    {
        // Arrange：构造一个 PTR 查询
        var msg = new MdnsMessage { Id = 0, Flags = 0x0000 };
        msg.Questions.Add(new MdnsQuestion
        {
            Name = ServiceType,
            Type = MdnsCodec.TypePTR,
            Class = MdnsCodec.ClassIN
        });

        // Act
        var bytes = MdnsCodec.Encode(msg);
        var decoded = MdnsCodec.Decode(bytes, bytes.Length);

        // Assert
        Assert.NotNull(decoded);
        Assert.False(decoded!.IsResponse);
        Assert.Single(decoded.Questions);
        Assert.Equal(ServiceType, decoded.Questions[0].Name);
        Assert.Equal(MdnsCodec.TypePTR, decoded.Questions[0].Type);
    }

    [Fact]
    public void Decode_MalformedPacket_ReturnsNull()
    {
        // 过短的数据包应返回 null（容错，不抛异常）
        var decoded = MdnsCodec.Decode(new byte[] { 1, 2 }, 2);
        Assert.Null(decoded);
    }

    [Fact]
    public void ParseTxt_MultipleEntries_AllParsed()
    {
        // 构造含多键值对的 TXT，验证长度前缀字符串解析
        var rdata = MdnsCodec.BuildTxt(new Dictionary<string, string>
        {
            ["id"] = "dev1",
            ["type"] = "Mobile",
            ["tls"] = "0"
        });

        var dict = MdnsCodec.ParseTxt(rdata);
        Assert.Equal(3, dict.Count);
        Assert.Equal("dev1", dict["id"]);
        Assert.Equal("Mobile", dict["type"]);
        Assert.Equal("0", dict["tls"]);
    }
}

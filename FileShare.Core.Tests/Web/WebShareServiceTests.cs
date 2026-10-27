using FileShare.Core.Web;
using System.Net.Http;
using System.Text;

namespace FileShare.Core.Tests.Web;

/// <summary>
/// 网页分享（浏览器上传）端到端测试：真实起一个 TCP 监听，用 HttpClient 走一遍页面与上传流程。
/// </summary>
public class WebShareServiceTests
{
    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Page_And_Upload_WorkEndToEnd()
    {
        var saveDirectory = Path.Combine(Path.GetTempPath(), "fs-webshare-" + Guid.NewGuid().ToString("N"));
        var port = GetFreePort();
        const string pin = "123456";

        using var service = new WebShareService();
        var received = new List<string>();
        service.FileReceived += path => received.Add(path);

        service.Start(new WebShareOptions
        {
            Port = port,
            Pin = pin,
            SaveDirectory = saveDirectory,
            PublicAddress = "127.0.0.1"
        });

        try
        {
            Assert.True(service.IsRunning);
            Assert.Equal($"http://127.0.0.1:{port}/", service.Url);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            // 1) 首页能拿到 HTML
            var page = await http.GetStringAsync("/");
            Assert.Contains("FileShare", page);
            Assert.Contains("upload", page);

            // 2) 错误 PIN 被拒绝
            using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/upload?name=a.txt")
            {
                Content = new StringContent("hello", Encoding.UTF8, "application/octet-stream")
            })
            {
                wrong.Headers.Add("X-Pin", "000000");
                using var response = await http.SendAsync(wrong);
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            }

            Assert.False(File.Exists(Path.Combine(saveDirectory, "a.txt")));

            // 3) 正确 PIN 上传成功
            using (var right = new HttpRequestMessage(HttpMethod.Post, "/upload?name=笔记.txt")
            {
                Content = new StringContent("你好，网页分享", Encoding.UTF8, "application/octet-stream")
            })
            {
                right.Headers.Add("X-Pin", pin);
                using var response = await http.SendAsync(right);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            var savedPath = Path.Combine(saveDirectory, "笔记.txt");
            Assert.True(File.Exists(savedPath), "上传的文件应落盘");
            Assert.Equal("你好，网页分享", await File.ReadAllTextAsync(savedPath));
            Assert.Contains(savedPath, received);

            // 4) 同名文件自动重命名，不覆盖
            using (var again = new HttpRequestMessage(HttpMethod.Post, "/upload?name=笔记.txt")
            {
                Content = new StringContent("second", Encoding.UTF8, "application/octet-stream")
            })
            {
                again.Headers.Add("X-Pin", pin);
                using var response = await http.SendAsync(again);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            Assert.True(File.Exists(Path.Combine(saveDirectory, "笔记 (1).txt")) || Directory.GetFiles(saveDirectory, "笔记*.txt").Length >= 2);
            Assert.Equal("你好，网页分享", await File.ReadAllTextAsync(savedPath));

            // 5) 目录穿越文件名被清洗
            using (var escape = new HttpRequestMessage(HttpMethod.Post, "/upload?name=" + Uri.EscapeDataString("../evil.txt"))
            {
                Content = new StringContent("x", Encoding.UTF8, "application/octet-stream")
            })
            {
                escape.Headers.Add("X-Pin", pin);
                using var response = await http.SendAsync(escape);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(saveDirectory)!, "evil.txt")));
            Assert.True(File.Exists(Path.Combine(saveDirectory, "evil.txt")));
        }
        finally
        {
            service.Stop();
            if (Directory.Exists(saveDirectory))
            {
                Directory.Delete(saveDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Start_RejectsEmptySaveDirectory()
    {
        using var service = new WebShareService();

        Assert.Throws<ArgumentException>(() => service.Start(new WebShareOptions
        {
            SaveDirectory = string.Empty
        }));
    }
}

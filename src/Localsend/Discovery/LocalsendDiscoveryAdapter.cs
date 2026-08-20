using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Localsend
{
    /// <summary>
    /// A discovery adapter compatible with LocalSend multicast v2.
    /// Listens on 224.0.0.167:53317 by default and parses MulticastMessageV2 JSON.
    /// Emits simple device notices via an event. Integrate with your discovery merge logic.
    /// </summary>
    public class LocalsendDiscoveryAdapter : IDisposable
    {
        private const string DefaultMulticastAddress = "224.0.0.167";
        private const int DefaultMulticastPort = 53317;
        private readonly IPEndPoint _multicastEndPoint;
        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private Task? _recvTask;
        private Task? _announceTask;

        // If set, will be used as the fingerprint field in announce messages (hex lower-case SHA256)
        public string? CertificateFingerprintHex { get; set; }

        public int AnnounceIntervalSeconds { get; set; } = 10;

        public event Action<LocalsendDeviceInfo>? DeviceDiscovered;

        public LocalsendDiscoveryAdapter(string? multicastAddress = null, int? multicastPort = null)
        {
            var addr = multicastAddress ?? DefaultMulticastAddress;
            var port = multicastPort ?? DefaultMulticastPort;
            _multicastEndPoint = new IPEndPoint(IPAddress.Parse(addr), port);
        }

        public async Task StartAsync()
        {
            if (_udp != null) return;

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _multicastEndPoint.Port));
            _udp.JoinMulticastGroup(_multicastEndPoint.Address);

            _cts = new CancellationTokenSource();
            _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _announceTask = Task.Run(() => AnnounceLoopAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();
                if (_recvTask != null) await _recvTask.ConfigureAwait(false);
                if (_announceTask != null) await _announceTask.ConfigureAwait(false);
            }
            catch { }
            try { _udp?.DropMulticastGroup(_multicastEndPoint.Address); } catch { }
            _udp?.Close();
            _udp?.Dispose();
            _udp = null;
            _cts?.Dispose();
            _cts = null;
        }

        private async Task AnnounceLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SendAnnouncementAsync(ct).ConfigureAwait(false);
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(AnnounceIntervalSeconds), ct).ConfigureAwait(false);
            }
        }

        private async Task SendAnnouncementAsync(CancellationToken ct)
        {
            if (_udp == null) return;
            var msg = BuildAnnounceMessage();
            var data = Encoding.UTF8.GetBytes(msg);
            await _udp.SendAsync(data, data.Length, _multicastEndPoint).ConfigureAwait(false);
        }

        private string BuildAnnounceMessage()
        {
            var v = new MulticastV2
            {
                alias = Environment.MachineName,
                version = "2.1",
                deviceModel = "FileShare",
                deviceType = "desktop",
                fingerprint = CertificateFingerprintHex ?? Guid.NewGuid().ToString(),
                port = 53317,
                protocol = "Https",
                download = true
            };
            return JsonSerializer.Serialize(v);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            if (_udp == null) return;
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try
                {
                    res = await _udp.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch { await Task.Delay(200, ct).ConfigureAwait(false); continue; }

                var json = Encoding.UTF8.GetString(res.Buffer);
                try
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var m = JsonSerializer.Deserialize<MulticastV2>(json, opts);
                    if (m == null) continue;

                    var di = new LocalsendDeviceInfo
                    {
                        Fingerprint = m.fingerprint,
                        Alias = m.alias,
                        Version = m.version,
                        DeviceModel = m.deviceModel,
                        DeviceType = m.deviceType,
                        Ip = res.RemoteEndPoint.Address.ToString(),
                        Port = m.port,
                        Protocol = m.protocol,
                        Download = m.download
                    };

                    DeviceDiscovered?.Invoke(di);
                }
                catch { /* ignore non-compatible messages */ }
            }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        private class MulticastV2
        {
            public string? alias { get; set; }
            public string? version { get; set; }
            public string? deviceModel { get; set; }
            public string? deviceType { get; set; }
            public string? fingerprint { get; set; }
            public int port { get; set; }
            public string? protocol { get; set; }
            public bool download { get; set; }
        }

        public class LocalsendDeviceInfo
        {
            public string? Fingerprint { get; set; }
            public string? Alias { get; set; }
            public string? Version { get; set; }
            public string? DeviceModel { get; set; }
            public string? DeviceType { get; set; }
            public string? Ip { get; set; }
            public int Port { get; set; }
            public string? Protocol { get; set; }
            public bool Download { get; set; }
        }
    }
}

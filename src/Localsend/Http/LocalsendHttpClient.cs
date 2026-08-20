using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileShare.Localsend.Http
{
    /// <summary>
    /// Minimal LocalSend v2 HTTP client: prepare-upload + upload with certificate pinning support.
    /// </summary>
    public class LocalsendHttpClient : IDisposable
    {
        private readonly HttpClient _http;

        public LocalsendHttpClient(string? expectedFingerprintHex = null)
        {
            if (!string.IsNullOrEmpty(expectedFingerprintHex))
            {
                var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                {
                    using var sha = SHA256.Create();
                    var actual = sha.ComputeHash(cert.GetRawCertData());
                    var actualHex = BitConverter.ToString(actual).Replace("-", "").ToLowerInvariant();
                    return string.Equals(actualHex, expectedFingerprintHex, StringComparison.OrdinalIgnoreCase);
                };
                _http = new HttpClient(handler);
            }
            else
            {
                _http = new HttpClient();
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        public async Task<PrepareResult?> PrepareUploadAsync(string baseUrl, IEnumerable<PrepareFile> files)
        {
            var dto = new { info = new { alias = Environment.MachineName, version = "2.1", device_model = "FileShare", device_type = "desktop" }, files };
            var json = JsonSerializer.Serialize(dto);
            var url = baseUrl.TrimEnd('/') + "/api/localsend/v2/prepare-upload";
            using var content = new StringContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var r = await JsonSerializer.DeserializeAsync<PrepareResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ConfigureAwait(false);
            if (r == null) return null;
            return new PrepareResult { SessionId = r.session_id, FileTokens = r.files };
        }

        public async Task<bool> UploadFileAsync(string baseUrl, string sessionId, string token, string filePath)
        {
            var url = baseUrl.TrimEnd('/') + "/api/localsend/v2/upload";
            using var fs = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(sessionId), "session_id");
            content.Add(new StringContent(token), "token");
            var streamContent = new StreamContent(fs);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "file", Path.GetFileName(filePath));
            var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        // DTOs
        public record PrepareFile(string id, string file_name, long size, string file_type);

        private class PrepareResponse
        {
            public string? session_id { get; set; }
            public Dictionary<string, TokenInfo>? files { get; set; }
        }
        private class TokenInfo { public string? token { get; set; } }

        public class PrepareResult
        {
            public string? SessionId { get; set; }
            public Dictionary<string, TokenInfo>? FileTokens { get; set; }
        }
    }
}

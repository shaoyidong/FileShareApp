using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// 统一管理应用所需的加密密钥、证书及 TOFU 信任库。
    /// 包含 Ed25519 签名密钥、RSA TLS 自签名证书和设备证书指纹库。
    /// 所有密钥持久化在应用数据目录下，启动时自动加载或生成。
    /// </summary>
    public sealed class CryptoManager
    {
        private const string PfxPassword = "FileShare_Tls_Cert_2026";
        private readonly string _baseDir;
        private static readonly Lazy<CryptoManager> _instance = new(() => new CryptoManager());

        public TlsCertificateStorageFormat StorageFormat { get; set; } = TlsCertificateStorageFormat.Pfx;

        public static CryptoManager Instance => _instance.Value;

        /// <summary>Ed25519 签名密钥</summary>
        public SigningTokenKey SigningKey { get; private set; }

        /// <summary>TLS 自签名证书</summary>
        public TlsCertificate TlsCertificate { get; private set; }

        /// <summary>TOFU 证书指纹信任库</summary>
        public FingerprintStore FingerprintStore { get; private set; }

        private X509Certificate2? _x509Certificate2;

        public X509Certificate2 GetX509Certificate2()
        {
            if (_x509Certificate2 == null)
            {
                _x509Certificate2 = TlsCertificate.LoadX509Certificate(PfxPassword);
            }
            return _x509Certificate2;
        }

        private CryptoManager()
        {
            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "FileShare";
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _baseDir = Path.Combine(appData, appName, "crypto");
            Directory.CreateDirectory(_baseDir);

            // 初始化所有组件
            LoadOrGenerateAll();
            // 指纹存储路径放在同一目录下
            string fingerprintPath = Path.Combine(_baseDir, "fingerprints.txt");
            FingerprintStore = new FingerprintStore(fingerprintPath);
        }

        public void Reload()
        {
            LoadOrGenerateAll();
            // 注意：FingerprintStore 不需要重新加载，其数据在每次 ValidateAndStore 时动态读写
            // 但如果需要重新实例化，可以在此处重新创建（但通常不需要）
        }

        private void LoadOrGenerateAll()
        {
            SigningKey = LoadOrGenerateSigningKey();
            TlsCertificate = LoadOrGenerateTlsCertificate();
        }

        private SigningTokenKey LoadOrGenerateSigningKey()
        {
            string keyPath = Path.Combine(_baseDir, "signing_key.pem");
            if (File.Exists(keyPath))
            {
                try
                {
                    var pem = File.ReadAllText(keyPath);
                    return SigningTokenKey.ParsePrivateKey(pem);
                }
                catch
                {
                    File.Delete(keyPath);
                }
            }

            var newKey = SigningTokenKey.Generate();
            File.WriteAllText(keyPath, newKey.ExportPrivateKey());
            return newKey;
        }

        private TlsCertificate LoadOrGenerateTlsCertificate()
        {
            if (StorageFormat == TlsCertificateStorageFormat.Pfx)
            {
                return LoadOrGeneratePfx();
            }
            else
            {
                return LoadOrGeneratePem();
            }
        }

        private TlsCertificate LoadOrGeneratePfx()
        {
            string pfxPath = Path.Combine(_baseDir, "tls.pfx");
            if (File.Exists(pfxPath))
            {
                try
                {
                    return TlsCertificate.FromPfx(File.ReadAllBytes(pfxPath), PfxPassword);
                }
                catch
                {
                    File.Delete(pfxPath);
                }
            }

            var newCert = TlsCertificate.Generate();
            byte[] pfxBytes = newCert.ExportPfx(PfxPassword);
            File.WriteAllBytes(pfxPath, pfxBytes);
            return newCert;
        }

        private TlsCertificate LoadOrGeneratePem()
        {
            string certPath = Path.Combine(_baseDir, "tls_cert.pem");
            string keyPath = Path.Combine(_baseDir, "tls_key.pem");
            if (File.Exists(certPath) && File.Exists(keyPath))
            {
                try
                {
                    var certPem1 = File.ReadAllText(certPath);
                    var keyPem1 = File.ReadAllText(keyPath);
                    return TlsCertificate.FromPem(certPem1, keyPem1);
                }
                catch
                {
                    File.Delete(certPath);
                    File.Delete(keyPath);
                }
            }

            var newCert = TlsCertificate.Generate();
            var (certPem, keyPem) = newCert.ExportPem();
            File.WriteAllText(certPath, certPem);
            File.WriteAllText(keyPath, keyPem);
            return newCert;
        }

        public void RegenerateAll()
        {
            foreach (var file in Directory.GetFiles(_baseDir))
            {
                File.Delete(file);
            }
            LoadOrGenerateAll();
            // 注意：指纹存储文件也被删除，需要重新生成？通常指纹数据是累积的，删除会导致所有已信任设备丢失，
            // 因此建议单独清理指纹文件或提供独立方法。
            // 这里我们不做自动删除，但可以调用 RegenerateFingerprintStore() 重置。
        }

        /// <summary>
        /// 重置指纹信任库（删除现有文件并新建空存储）。
        /// </summary>
        public void ResetFingerprintStore()
        {
            string path = Path.Combine(_baseDir, "fingerprints.txt");
            if (File.Exists(path)) File.Delete(path);
            FingerprintStore = new FingerprintStore(path);
        }
    }
}
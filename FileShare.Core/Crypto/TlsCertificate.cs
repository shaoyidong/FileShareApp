using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.IO.Pem;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// 自签名 RSA TLS 证书（X.509），支持 PEM / PFX 导出。
    /// </summary>
    public sealed class TlsCertificate
    {
        private readonly Org.BouncyCastle.X509.X509Certificate _certificate;
        private readonly AsymmetricKeyParameter _privateKey;

        private TlsCertificate(Org.BouncyCastle.X509.X509Certificate certificate, AsymmetricKeyParameter privateKey)
        {
            _certificate = certificate;
            _privateKey = privateKey;
        }

        /// <summary>
        /// 证书指纹：SHA-256(DER) 大写十六进制。
        /// </summary>
        public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.GetEncoded()));

        /// <summary>
        /// 导出为 PEM 格式（证书 + 私钥）。
        /// </summary>
        public (string CertPem, string PrivateKeyPem) ExportPem()
        {
            string certPem = ExportCertificatePem(_certificate);
            string keyPem = ExportPrivateKeyPem(_privateKey);
            return (certPem, keyPem);
        }

        /// <summary>
        /// 导出为 PFX (PKCS#12) 字节数组，使用指定密码保护。
        /// </summary>
        public byte[] ExportPfx(string password)
        {
            var storeBuilder = new Pkcs12StoreBuilder();
            var store = storeBuilder.Build();

            var alias = _certificate.SubjectDN.ToString();
            var certificateEntry = new X509CertificateEntry(_certificate);
            // 设置证书条目
            store.SetCertificateEntry(alias, certificateEntry);
            // 设置私钥条目（关联证书链）
            store.SetKeyEntry(alias, new AsymmetricKeyEntry(_privateKey), new[] { certificateEntry });

            using var ms = new MemoryStream();
            store.Save(ms, password.ToCharArray(), new SecureRandom());
            return ms.ToArray();
        }

        /// <summary>
        /// 加载为 .NET 的 X509Certificate2（包含私钥），可用于配置 HTTPS 或 mTLS。
        /// 内部通过 PFX 临时转换（因为 .NET 对 PEM+私钥的加载支持有限）。
        /// </summary>
        public X509Certificate2 LoadX509Certificate(string password)
        {
            byte[] pfx = ExportPfx(password);
            return X509CertificateLoader.LoadPkcs12(pfx, password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }

        // ----- 静态工厂方法 -----

        /// <summary>
        /// 生成新的自签名 RSA 证书（有效期 10 年）。
        /// </summary>
        public static TlsCertificate Generate()
        {
            var keyPair = GenerateRsaKeyPair(2048);
            var cert = CreateSelfSignedCertificate(keyPair, 365 * 10);
            return new TlsCertificate(cert, keyPair.Private);
        }

        /// <summary>
        /// 从 PEM 格式加载证书和私钥。
        /// </summary>
        public static TlsCertificate FromPem(string certPem, string keyPem)
        {
            var cert = LoadCertificateFromPem(certPem);
            var privateKey = LoadPrivateKeyFromPem(keyPem);
            return new TlsCertificate(cert, privateKey);
        }        

        /// <summary>
        /// 从 PFX (PKCS#12) 字节数组或文件路径加载证书和私钥。
        /// </summary>
        public static TlsCertificate FromPfx(byte[] pfxData, string password)
        {
            var store = new Pkcs12StoreBuilder().Build();   // 使用 Builder 创建空 store
            using var ms = new MemoryStream(pfxData);
            store.Load(ms, password.ToCharArray());         // 加载 PFX 内容

            string? alias = null;
            Org.BouncyCastle.X509.X509Certificate? cert = null;
            AsymmetricKeyParameter? privateKey = null;

            // 遍历所有别名，找到包含私钥的证书条目
            foreach (string a in store.Aliases)
            {
                if (store.IsKeyEntry(a) && store.GetKey(a).Key is AsymmetricKeyParameter key)
                {
                    privateKey = key;
                    alias = a;
                    break;
                }
            }

            if (alias == null || privateKey == null)
                throw new InvalidDataException("No private key found in PFX.");

            // 获取对应证书（通常第一个证书就是）
            var chain = store.GetCertificateChain(alias);
            if (chain == null || chain.Length == 0)
                throw new InvalidDataException("No certificate found in PFX.");
            cert = chain[0].Certificate;

            return new TlsCertificate(cert, privateKey);
        }

        

        // ----- 私有辅助方法（静态） -----

        private static AsymmetricCipherKeyPair GenerateRsaKeyPair(int strength)
        {
            var generator = new RsaKeyPairGenerator();
            var param = new KeyGenerationParameters(new SecureRandom(), strength);
            generator.Init(param);
            return generator.GenerateKeyPair();
        }

        private static Org.BouncyCastle.X509.X509Certificate CreateSelfSignedCertificate(AsymmetricCipherKeyPair keyPair, int validityDays)
        {
            var random = new SecureRandom();
            // 序列号：8 字节随机数
            var serialNumber = GenerateSerialNumber(random);
            var subjectName = new X509Name("CN=LocalSend User");
            var issuerName = subjectName; // 自签名
            var notBefore = DateTime.UtcNow.AddDays(-1);
            var notAfter = DateTime.UtcNow.AddDays(validityDays);

            var generator = new X509V3CertificateGenerator();
            generator.SetSerialNumber(serialNumber);
            generator.SetSubjectDN(subjectName);
            generator.SetIssuerDN(issuerName);
            generator.SetNotBefore(notBefore);
            generator.SetNotAfter(notAfter);
            generator.SetPublicKey(keyPair.Public);

            // 添加扩展（与 LocalSend Dart 端行为一致，增加 mTLS 所需）
            // 1. 基本约束：非 CA
            generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
            // 2. 扩展密钥用法：服务器认证 + 客户端认证（mTLS 双向认证）
            generator.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(new[] { KeyPurposeID.id_kp_serverAuth, KeyPurposeID.id_kp_clientAuth }));
            // 3. 使用者密钥标识（可选，但增加兼容性）使用者密钥标识符 (SKI) - 使用 X509ExtensionUtilities 避免过时警告
            var ski = X509ExtensionUtilities.CreateSubjectKeyIdentifier(keyPair.Public);
            generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, ski);

            var signatureFactory = new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random);
            return generator.Generate(signatureFactory);
        }

        private static string ExportCertificatePem(Org.BouncyCastle.X509.X509Certificate cert)
        {
            using var sw = new StringWriter();
            using var pw = new PemWriter(sw);
            pw.WriteObject(new PemObject("CERTIFICATE", cert.GetEncoded()));
            return sw.ToString();
        }

        private static string ExportPrivateKeyPem(AsymmetricKeyParameter privateKey)
        {
            // 导出为 PKCS#8 格式
            var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey);
            using var sw = new StringWriter();
            using var pw = new PemWriter(sw);
            pw.WriteObject(new PemObject("PRIVATE KEY", privateKeyInfo.GetEncoded()));
            return sw.ToString();
        }

        private static Org.BouncyCastle.X509.X509Certificate LoadCertificateFromPem(string pem)
        {
            using var sr = new StringReader(pem);
            using var pr = new PemReader(sr);
            var pemObj = pr.ReadPemObject();
            if (pemObj.Type != "CERTIFICATE")
                throw new InvalidDataException("PEM does not contain a certificate.");
            return new X509CertificateParser().ReadCertificate(pemObj.Content);
        }

        private static AsymmetricKeyParameter LoadPrivateKeyFromPem(string pem)
        {
            using var sr = new StringReader(pem);
            using var pr = new PemReader(sr);
            var pemObj = pr.ReadPemObject();
            if (pemObj.Type != "PRIVATE KEY")
                throw new InvalidDataException("PEM does not contain a private key.");
            var privateKeyInfo = PrivateKeyInfo.GetInstance(pemObj.Content);
            return PrivateKeyFactory.CreateKey(privateKeyInfo);
        }        

        private static BigInteger GenerateSerialNumber(SecureRandom random)
        {
            // 生成 64 位（8 字节）随机数，范围 [0, 2^64 - 1]
            // 循环排除 0，保证严格为正整数且分布绝对均匀
            BigInteger serialNumber;
            do
            {
                serialNumber = new BigInteger(64, random);
            } while (serialNumber.SignValue == 0);

            return serialNumber;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Crypto
{
    public enum TlsCertificateStorageFormat
    {
        Pem,   // 两个文件：tls_cert.pem + tls_key.pem
        Pfx    // 一个文件：tls.pfx（带密码保护）
    }
}

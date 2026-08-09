using System.Runtime.CompilerServices;

// 暴露内部成员给测试项目，便于对实现细节（如 mDNS 报文编解码器）进行单元测试
[assembly: InternalsVisibleTo("FileShare.Core.Tests")]

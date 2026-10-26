using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FileShare.Core.Common
{
    [JsonSerializable(typeof(TransferRequest))]
    [JsonSerializable(typeof(TransferResponse))]
    [JsonSerializable(typeof(DeviceInfo))]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class SourceGenerationContext:JsonSerializerContext
    {
    }
}

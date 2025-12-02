using FileShare.Core.Models;
using FileShare.Core.Network;
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
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class SourceGenerationContext:JsonSerializerContext
    {
    }
}

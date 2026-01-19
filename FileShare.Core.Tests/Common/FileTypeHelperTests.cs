using FileShare.Core.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Tests.Common
{
    public class FileTypeHelperTests
    {
        [Fact]
        public void GetDownloadsPathCrossPlatformDummyTest()
        {
            var str = FileTypeHelper.GetDownloadsPathCrossPlatform();
            Assert.NotNull(str);
            Assert.NotEmpty(str);
        }
    }
}

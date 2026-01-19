using FileShare.Core.Common;
using FileShare.Core.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Tests.Common
{
    public class FileTypeHelperTests
    {
        // 模拟的IPlatformDirectoryService实现
        private class MockPlatformDirectoryService : IPlatformDirectoryService
        {
            public string GetDownloadsDirectory() => "C:\\Downloads";
            public string GetPicturesDirectory() => "C:\\Pictures";
            public string GetVideosDirectory() => "C:\\Videos";
            public string GetMusicDirectory() => "C:\\Music";
            public string GetDocumentsDirectory() => "C:\\Documents";
        }

        [Fact]
        public void GetDirectoryByFileType_ReturnsCorrectDirectory()
        {
            var mockService = new MockPlatformDirectoryService();
            
            // 测试图片文件
            var imagePath = FileTypeHelper.GetDirectoryByFileType("test.jpg", mockService);
            Assert.Equal("C:\\Pictures", imagePath);
            
            // 测试视频文件
            var videoPath = FileTypeHelper.GetDirectoryByFileType("test.mp4", mockService);
            Assert.Equal("C:\\Videos", videoPath);
            
            // 测试音频文件
            var audioPath = FileTypeHelper.GetDirectoryByFileType("test.mp3", mockService);
            Assert.Equal("C:\\Music", audioPath);
            
            // 测试文档文件
            var docPath = FileTypeHelper.GetDirectoryByFileType("test.pdf", mockService);
            Assert.Equal("C:\\Documents", docPath);
            
            // 测试其他文件（默认下载目录）
            var otherPath = FileTypeHelper.GetDirectoryByFileType("test.xyz", mockService);
            Assert.Equal("C:\\Downloads", otherPath);
        }
    }
}

using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Pure formatting helpers used throughout the UI for reporting recovered/migrated space.
    public class FormattingTests
    {
        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(1536, "2 KB")]            // 1.5 KB rounds to "2 KB" (F0)
        [InlineData(1048576, "1.0 MB")]
        [InlineData(1073741824, "1.00 GB")]
        public void StorageMigrator_FormatBytes_FormatsHumanReadable(long bytes, string expected)
        {
            Assert.Equal(expected, StorageMigrator.FormatBytes(bytes));
        }

        [Theory]
        [InlineData(0, "0 Bytes")]
        [InlineData(2048, "2 KB")]
        [InlineData(5 * 1024 * 1024, "5.0 MB")]
        public void ScanItem_SizeDisplay_FormatsHumanReadable(long bytes, string expected)
        {
            var item = new ScanItem { TotalSizeBytes = bytes };
            Assert.Equal(expected, item.SizeDisplay);
        }
    }
}

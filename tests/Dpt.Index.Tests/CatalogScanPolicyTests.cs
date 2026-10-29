namespace Dpt.Index.Tests;

public class CatalogScanPolicyTests
{
    [Theory]
    [InlineData(@"C:\", true, false, CatalogScanKind.ServeSnapshot)]
    [InlineData(@"C:", true, false, CatalogScanKind.ServeSnapshot)]
    [InlineData(@"D:\", true, false, CatalogScanKind.ServeSnapshot)]
    [InlineData(@"C:\", true, true, CatalogScanKind.ThrottledWalk)]
    [InlineData(@"C:\", false, false, CatalogScanKind.ThrottledWalk)]
    [InlineData(@"E:\", false, true, CatalogScanKind.ThrottledWalk)]
    public void Whole_volumes_serve_a_snapshot_and_otherwise_walk_once(string root, bool hasSnapshot, bool explicitRebuild, CatalogScanKind expected)
    {
        var kind = CatalogScanPolicy.Decide(root, hasSnapshot, explicitRebuild);
        Assert.Equal(expected, kind);
        Assert.False(CatalogScanPolicy.Watches(kind));
    }

    [Theory]
    [InlineData(@"C:\data\photos", true, false)]
    [InlineData(@"C:\data\photos", false, false)]
    [InlineData(@"C:\data\photos\", false, true)]
    public void Folder_roots_stay_watched(string root, bool hasSnapshot, bool explicitRebuild)
    {
        var kind = CatalogScanPolicy.Decide(root, hasSnapshot, explicitRebuild);
        Assert.Equal(CatalogScanKind.WatchedFolder, kind);
        Assert.True(CatalogScanPolicy.Watches(kind));
    }

    [Fact]
    public void Watcher_buffer_is_64_kilobytes() => Assert.Equal(65536, CatalogScanPolicy.WatcherBufferBytes);
}

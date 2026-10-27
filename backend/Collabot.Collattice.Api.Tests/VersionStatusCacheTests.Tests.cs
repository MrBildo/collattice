using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class VersionStatusCacheTests
{
    [Fact]
    public void GetStatus_BeforeAnyPoll_ReportsCurrentOnly_NoUpdate()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));

        var status = cache.GetStatus();

        status.Current.ShouldBe("1.16.0");
        status.Latest.ShouldBeNull();
        status.UpdateAvailable.ShouldBeFalse();
        status.ReleaseUrl.ShouldBeNull();
        status.LastChecked.ShouldBeNull();
    }

    [Fact]
    public void GetStatus_LatestGreaterThanCurrent_UpdateAvailable()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));
        var checkedAt = DateTimeOffset.UtcNow;

        cache.SetLatest(new LatestVersionResult("v1.17.0", "https://example.test/release"), checkedAt);

        var status = cache.GetStatus();

        status.Current.ShouldBe("1.16.0");
        status.Latest.ShouldBe("1.17.0");
        status.UpdateAvailable.ShouldBeTrue();
        status.ReleaseUrl.ShouldBe("https://example.test/release");
        status.LastChecked.ShouldBe(checkedAt);
    }

    [Fact]
    public void GetStatus_LatestEqualsCurrent_NoUpdate()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));

        cache.SetLatest(new LatestVersionResult("1.16.0", "https://example.test/release"), DateTimeOffset.UtcNow);

        cache.GetStatus().UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public void GetStatus_LatestLessThanCurrent_NoUpdate()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));

        cache.SetLatest(new LatestVersionResult("1.15.0", "https://example.test/release"), DateTimeOffset.UtcNow);

        cache.GetStatus().UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public void GetStatus_DevSentinelCurrent_NeverNags_EvenWhenLatestIsHigher()
    {
        var cache = new VersionStatusCache(SemVer.DevSentinel);

        cache.SetLatest(new LatestVersionResult("v9.9.9", "https://example.test/release"), DateTimeOffset.UtcNow);

        var status = cache.GetStatus();

        status.Current.ShouldBe("0.0.0");
        status.Latest.ShouldBe("9.9.9");
        status.UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public void GetStatus_MalformedLatestTag_NoUpdate_NoThrow()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));

        cache.SetLatest(new LatestVersionResult("not-a-version", "https://example.test/release"), DateTimeOffset.UtcNow);

        var status = cache.GetStatus();

        status.Latest.ShouldBeNull();
        status.UpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public void SetLatest_SecondCall_OverwritesAndKeepsLastChecked()
    {
        var cache = new VersionStatusCache(new SemVer(1, 16, 0));
        var firstAt = DateTimeOffset.UtcNow.AddHours(-1);
        var secondAt = DateTimeOffset.UtcNow;

        cache.SetLatest(new LatestVersionResult("1.17.0", "https://example.test/a"), firstAt);
        cache.SetLatest(new LatestVersionResult("1.18.0", "https://example.test/b"), secondAt);

        var status = cache.GetStatus();

        status.Latest.ShouldBe("1.18.0");
        status.ReleaseUrl.ShouldBe("https://example.test/b");
        status.LastChecked.ShouldBe(secondAt);
    }
}

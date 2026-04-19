using ScrapBot.Steam;
using SteamKit2;

namespace ScrapBot.Tests;

public class PICSChangeLogicTests
{
    [Fact]
    public void ShouldSkipChange_SkipsWhenReviewPercentageChanges()
    {
        var ratings = new Dictionary<uint, string?> { [387990] = "90" };
        var tags = new Dictionary<uint, Dictionary<string, string>>
        {
            [387990] = new() { ["0"] = "1643" }
        };

        var result = PICSChangeLogic.ShouldSkipChange(387990, BuildPics("91", "1643"), ratings, tags);

        Assert.True(result);
        Assert.Equal("91", ratings[387990]);
        Assert.Equal("1643", tags[387990]["0"]);
    }

    [Fact]
    public void ShouldSkipChange_SkipsWhenStoreTagIdsChange()
    {
        var ratings = new Dictionary<uint, string?> { [387990] = "90" };
        var tags = new Dictionary<uint, Dictionary<string, string>>
        {
            [387990] = new() { ["0"] = "1643" }
        };

        var result = PICSChangeLogic.ShouldSkipChange(387990, BuildPics("90", "3810"), ratings, tags);

        Assert.True(result);
        Assert.Equal("3810", tags[387990]["0"]);
    }

    [Fact]
    public void ShouldSkipChange_ReturnsFalseWhenMetadataIsUnchanged()
    {
        var ratings = new Dictionary<uint, string?> { [387990] = "90" };
        var tags = new Dictionary<uint, Dictionary<string, string>>
        {
            [387990] = new() { ["0"] = "1643", ["1"] = "3810" }
        };

        var result = PICSChangeLogic.ShouldSkipChange(
            387990,
            BuildPics("90", "1643", "3810"),
            ratings,
            tags);

        Assert.False(result);
        Assert.Equal(2, tags[387990].Count);
    }

    [Fact]
    public void ShouldSkipChange_ReturnsTrueWhenAppHasNoStoredTags()
    {
        var ratings = new Dictionary<uint, string?> { [387990] = "90" };
        var tags = new Dictionary<uint, Dictionary<string, string>>();

        var result = PICSChangeLogic.ShouldSkipChange(387990, BuildPics("90", "1643"), ratings, tags);

        Assert.True(result);
        Assert.True(tags.ContainsKey(387990));
        Assert.Equal("1643", tags[387990]["0"]);
    }

    [Fact]
    public void ShouldSkipChange_SkipsWhenStoreTagOrderingChanges()
    {
        var ratings = new Dictionary<uint, string?> { [387990] = "91" };
        var tags = new Dictionary<uint, Dictionary<string, string>>
        {
            [387990] = new() { ["0"] = "1643", ["1"] = "3810" }
        };

        var result = PICSChangeLogic.ShouldSkipChange(387990, BuildPics("91", "3810", "1643"), ratings, tags);

        Assert.True(result);
        Assert.Equal("3810", tags[387990]["0"]);
        Assert.Equal("1643", tags[387990]["1"]);
    }
    private static KeyValue BuildPics(string? reviewPercentage, params string[] storeTagIds)
    {
        var root = new KeyValue("app");
        var common = new KeyValue("common");

        if (reviewPercentage is not null)
        {
            common.Children.Add(new KeyValue("review_percentage", reviewPercentage));
        }

        var storeTags = new KeyValue("store_tags");
        for (var i = 0; i < storeTagIds.Length; i++)
        {
            storeTags.Children.Add(new KeyValue(i.ToString(), storeTagIds[i]));
        }

        common.Children.Add(storeTags);
        root.Children.Add(common);

        return root;
    }
}

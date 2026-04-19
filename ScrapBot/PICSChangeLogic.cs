using SteamKit2;

namespace ScrapBot.Steam;

internal static class PICSChangeLogic
{
    internal static bool ShouldSkipChange(
        uint appid,
        KeyValue? pics,
        IDictionary<uint, string?> steamRating,
        IDictionary<uint, Dictionary<string, string>> storeTags)
    {
        if (pics is null)
        {
            return false;
        }

        var ratingCurrent = pics["common"]["review_percentage"]?.Value;
        var tagsCurrent = pics["common"]["store_tags"].ToDict();

        if (ratingCurrent is not null && steamRating[appid] != ratingCurrent)
        {
            steamRating[appid] = ratingCurrent;
            return true;
        }

        if (tagsCurrent is null)
        {
            return false;
        }

        if (!storeTags.ContainsKey(appid))
        {
            storeTags[appid] = tagsCurrent;
            return true;
        }

        var tagsOld = storeTags[appid];
        var diffKeys = false;
        var sameValues = tagsCurrent.Values.All(tagsOld.Values.Contains);
        if (!sameValues)
        {
            storeTags[appid] = tagsCurrent;
            return true;
        }

        foreach (var (k, v) in tagsCurrent)
        {
            tagsOld.TryGetValue(k, out var v2);
            if (v != v2)
            {
                diffKeys = true;
                break;
            }
        }

        if (tagsCurrent.Count != tagsOld.Count || diffKeys)
        {
            storeTags[appid] = tagsCurrent;
            return true;
        }

        return false;
    }
}

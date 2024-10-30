using SteamKit2;

namespace ScrapBot;

public static class KeyValueExtensions
{
    public static Dictionary<string, string>? GetStoreTagsIfExists(this KeyValue keyValue)
    {
        var store_tags = keyValue.CustomIndex("common/store_tags");
        if (store_tags is null) return null;

        var res = new Dictionary<string, string>();
        foreach (var kv in store_tags.Children)
        {
            res.Add(kv.Name!, kv.Value!);
        }
        return res;
    }
    public static string PrintString(this KeyValue keyValue)
    {
        var returnString = "";
        if (keyValue.Value == null)
        {
            var children = string.Join(",\n", keyValue.Children.ConvertAll(kv => $"\t{PrintString(kv)}"));
            returnString = $"\"{keyValue.Name}\": {{ \n{children} \n}}";
        }
        else
        {
            returnString = $"\"{keyValue.Name}\" : \"{keyValue.Value}\"";
        }

        return returnString;
    }
    public static KeyValue? CustomIndex(this KeyValue kv, string index)
    {
        if (index == "") return kv;

        var indexes = index.Split("/");
        var i = indexes[0];

        var foundChildren = kv.Children.Where(kv => kv.Name == i);
        if (foundChildren.ToArray().Length == 0) return null;
        var firstFoundChild = foundChildren.ToArray()[0];

        string nextIndex = string.Join("/", index.Split("/").Skip(1));
        return CustomIndex(firstFoundChild, nextIndex);
    }
}


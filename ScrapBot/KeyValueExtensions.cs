using SteamKit2;

namespace ScrapBot;

public static class KeyValueExtensions
{
    public static Dictionary<string, string>? ToDict(this KeyValue keyValue)
    {
        var res = new Dictionary<string, string>();
        foreach (var kv in keyValue.Children)
        {
            res.Add(kv.Name!, kv.Value!);
        }
        return res;
    }
    public static string PrintString(this KeyValue keyValue)
    {
        return PrintString(keyValue, 0);
    }

    private static string PrintString(KeyValue keyValue, int depth)
    {
        var indent = new string('\t', depth);
        if (keyValue.Value is null)
        {
            var children = string.Join("\n", keyValue.Children
                .Select(kv => PrintString(kv, depth + 1)));
            return $"{indent}\"{keyValue.Name}\": {{\n{children}\n{indent}}}";
        }
        return $"{indent}\"{keyValue.Name}\": \"{keyValue.Value}\"";
    }
}


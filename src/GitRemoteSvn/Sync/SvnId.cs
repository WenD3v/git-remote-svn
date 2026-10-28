namespace GitRemoteSvn.Sync;

public static class SvnId
{
    public const string Prefix = "svn-id: ";

    public static string Format(string url, long rev, string uuid) => $"{Prefix}{url}@{rev} {uuid}";

    public static bool TryParse(string message, out string url, out long rev)
    {
        url = "";
        rev = 0;
        var line = message.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return false;
        var rest = line.Substring(Prefix.Length);
        var at = rest.LastIndexOf('@');
        if (at <= 0) return false;
        var space = rest.IndexOf(' ', at);
        if (space < 0) return false;
        if (!long.TryParse(rest.Substring(at + 1, space - at - 1), out rev)) return false;
        url = rest.Substring(0, at);
        return true;
    }
}

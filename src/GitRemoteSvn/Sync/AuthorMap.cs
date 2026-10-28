using System.Text.RegularExpressions;

namespace GitRemoteSvn.Sync;

public class AuthorMap
{
    readonly Dictionary<string, (string Name, string Email)> _map;

    AuthorMap(Dictionary<string, (string, string)> map) => _map = map!;

    public static AuthorMap Load(string? path)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (path is not null && File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var m = Regex.Match(line, @"^\s*([^=#]+?)\s*=\s*(.*?)\s*<([^>]+)>\s*$");
                if (m.Success) map[m.Groups[1].Value.Trim()] = (m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim());
            }
        }
        return new AuthorMap(map);
    }

    public (string Name, string Email) Resolve(string svnUser) =>
        _map.TryGetValue(svnUser, out var v) ? v : (svnUser, $"{svnUser}@svn.local");
}

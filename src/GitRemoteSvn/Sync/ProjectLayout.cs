using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

public record ProjectLayout(string RootUrl, string TrunkUrl, bool Standard,
    IReadOnlyList<string> Branches, IReadOnlyList<string> Tags)
{
    public string BranchesUrl => RootUrl + "/branches";
    public string TagsUrl => RootUrl + "/tags";

    public static async Task<ProjectLayout> DiscoverAsync(SvnClient svn, string url)
    {
        url = url.TrimEnd('/');
        var entries = await svn.ListAsync(url);
        var names = entries.Where(e => e.Kind == "dir").Select(e => e.Name).ToHashSet();
        if (!names.Contains("trunk"))
            return new ProjectLayout(url, url, false, Array.Empty<string>(), Array.Empty<string>());

        var branches = names.Contains("branches")
            ? (await svn.ListAsync(url + "/branches")).Where(e => e.Kind == "dir").Select(e => e.Name).OrderBy(n => n).ToList()
            : new List<string>();
        var tags = names.Contains("tags")
            ? (await svn.ListAsync(url + "/tags")).Where(e => e.Kind == "dir").Select(e => e.Name).OrderBy(n => n).ToList()
            : new List<string>();
        return new ProjectLayout(url, url + "/trunk", true, branches, tags);
    }
}

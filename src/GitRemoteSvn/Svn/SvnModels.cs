namespace GitRemoteSvn.Svn;

public record SvnEntry(string Name, string Kind, long Revision);

public record SvnPathChange(char Action, string Path, string? CopyFromPath, long? CopyFromRev, string Kind);

public record SvnRevision(long Revision, string Author, DateTimeOffset Date, string Message,
    IReadOnlyList<SvnPathChange> Paths);

public record SvnInfo(string Url, string RepoRootUrl, string Uuid, long Revision);

public class SvnException(string message) : Exception(message);

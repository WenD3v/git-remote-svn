using System.Diagnostics;
using System.Text;

namespace GitRemoteSvn.Git;

public record ImportFile(string Path, byte[]? Content);

public sealed class FastImportWriter : IAsyncDisposable
{
    readonly Process _p;
    readonly Stream _in;
    readonly GitClient _git;
    int _mark;

    internal FastImportWriter(GitClient git)
    {
        _git = git;
        _p = FastImportProcess.Start(git.GitExe, git.RepoPath);
        _in = _p.StandardInput.BaseStream;
    }

    async Task WriteAsync(string text) => await _in.WriteAsync(Encoding.UTF8.GetBytes(text));

    async Task DataAsync(byte[] bytes)
    {
        await WriteAsync($"data {bytes.Length}\n");
        await _in.WriteAsync(bytes);
        await WriteAsync("\n");
    }

    public async Task CommitAsync(string refName, string? fromRef,
        (string Name, string Email, DateTimeOffset Date) author, string message,
        IReadOnlyList<ImportFile> files)
    {
        var stamp = $"{author.Date.ToUnixTimeSeconds()} +0000";
        await WriteAsync($"commit {refName}\n");
        _mark++;
        await WriteAsync($"mark :{_mark}\n");
        await WriteAsync($"author {author.Name} <{author.Email}> {stamp}\n");
        await WriteAsync($"committer {author.Name} <{author.Email}> {stamp}\n");
        await DataAsync(Encoding.UTF8.GetBytes(message));
        if (!string.IsNullOrEmpty(fromRef))
        {
            if (fromRef != refName || await _git.RefExistsAsync(refName))
            {
                var baseSha = await _git.RevParseAsync(fromRef);
                await WriteAsync($"from {baseSha}\n");
            }
        }
        foreach (var f in files)
        {
            if (f.Content is null)
            {
                await WriteAsync($"D {f.Path}\n");
            }
            else
            {
                await WriteAsync($"M 100644 inline {f.Path}\n");
                await DataAsync(f.Content);
            }
        }
        await WriteAsync("\n");
        await _in.FlushAsync();
    }

    public async Task MarkAsync(string refName, string sha)
    {
        await WriteAsync($"reset {refName}\nfrom {sha}\n\n");
        await _in.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await WriteAsync("done\n");
        await _in.FlushAsync();
        _p.StandardInput.Close();
        var stderr = await _p.StandardError.ReadToEndAsync();
        await _p.WaitForExitAsync();
        if (_p.ExitCode != 0) throw new InvalidOperationException($"git fast-import: {stderr.Trim()}");
        _p.Dispose();
    }
}

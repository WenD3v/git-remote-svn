using GitRemoteSvn.Svn;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class SvnRetryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "grs-retry-" + Guid.NewGuid().ToString("N"));

    /// <summary>svn falso: as primeiras <paramref name="falhas"/> chamadas falham com timeout de rede; depois delega ao svn real.</summary>
    string FakeSvn(int falhas)
    {
        Directory.CreateDirectory(_dir);
        var counter = Path.Combine(_dir, "count.txt");
        var script = Path.Combine(_dir, "svn.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"set /p N=<\"{counter}\"\r\n" +
            "set /a N=N+1\r\n" +
            $">\"{counter}\" echo %N%\r\n" +
            $"if %N% LEQ {falhas} (echo svn: E175012: Connection timed out 1>&2 & exit /b 1)\r\n" +
            "svn %*\r\n");
        File.WriteAllText(counter, "0");
        return script;
    }

    int Calls => int.Parse(File.ReadAllText(Path.Combine(_dir, "count.txt")).Trim());

    [Fact]
    public async Task Cat_tenta_de_novo_apos_timeout_de_rede()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "conteudo"u8.ToArray(), "a");
        var svn = new SvnClient(FakeSvn(falhas: 2)) { RetryDelay = TimeSpan.Zero };

        var bytes = await svn.CatAsync(repo.RepoUrl + "/trunk/a.txt@HEAD");

        Assert.Equal("conteudo"u8.ToArray(), bytes);
        Assert.Equal(3, Calls);
    }

    [Fact]
    public async Task Erro_que_nao_e_de_rede_nao_repete()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var repo = await SvnTestRepo.CreateAsync();
        var svn = new SvnClient(FakeSvn(falhas: 0)) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<SvnException>(() => svn.InfoAsync(repo.RepoUrl + "/nao-existe"));

        Assert.Equal(1, Calls);
    }

    [Fact]
    public async Task Desiste_depois_de_varias_falhas_de_rede()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var repo = await SvnTestRepo.CreateAsync();
        var svn = new SvnClient(FakeSvn(falhas: 100)) { RetryDelay = TimeSpan.Zero };

        var ex = await Assert.ThrowsAsync<SvnException>(() => svn.InfoAsync(repo.RepoUrl));

        Assert.Contains("E175012", ex.Message);
        Assert.Equal(SvnClient.MaxAttempts, Calls);
    }

    public void Dispose() => SvnTestRepo.DeleteTree(_dir);
}

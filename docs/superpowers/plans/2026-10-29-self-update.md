# Self-update do git-remote-svn — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** dar ao helper `--version` e `update [--check] [--force]`, mais checagem diária em cache com aviso e auto-instalação opt-in, para quem instalou pelo zip conseguir atualizar sem fonte.

**Architecture:** um pacote `Update/` sem dependência de SVN: `VersionUtil` (comparação), `UpdateCache` (arquivo do usuário com TTL), `UpdateOptions` (ambiente + `git config`), `UpdateService` (consulta o release no GitHub, baixa, confere SHA-256, extrai e troca o executável). O `Program.cs` desvia os comandos antes do protocolo e chama a checagem no fim da sessão.

**Tech Stack:** .NET 9, `HttpClient` (injetável), `System.Text.Json`, `System.IO.Compression`, `System.Formats.Tar`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-29-self-update-design.md`

## Global Constraints

- Nenhuma falha de rede pode mudar o resultado de um `fetch`/`push`; toda exceção da checagem é engolida.
- A checagem só consulta o GitHub quando o cache (`24h`) venceu.
- `SHA256SUMS` é obrigatório; hash divergente ou ausente = nada é instalado.
- Windows: renomear o exe em uso para `<exe>.old`, gravar o novo, apagar `.old` na próxima execução. Linux: `.new` + `chmod 0755` + `File.Move(overwrite: true)`.
- Instalação de desenvolvimento (`Environment.ProcessPath` é `dotnet`/`dotnet.exe`) é recusada por `update`.
- Comentários, mensagens de commit e docs em português; README em inglês + `README.pt-BR.md`.
- Suíte atual: 75 testes verdes. `dotnet test -c Release` precisa terminar verde em cada tarefa.

## Review Focus

- **Executável em uso (Windows):** trocar o arquivo que está rodando não pode corromper a instalação nem deixar dois binários; o esperado é a sessão atual terminar e a próxima usar a versão nova.
- **Servidor sem SHA256SUMS (releases v1.0.0/v1.0.1):** `update` deve recusar com mensagem clara, não instalar às cegas.
- **Asset inexistente para a plataforma:** "sem atualização para esta plataforma", nunca baixar o zip do Windows no Linux.
- **Cache velho/corrompido:** JSON inválido, data no futuro ou arquivo apagado no meio = tratar como "nunca checado".
- **Diretório sem permissão de escrita:** erro explícito com o caminho, sem deixar `.old` órfão nem exe truncado.

---

### Task 1: Versão (`VersionUtil`)

**Files:**
- Create: `src/GitRemoteSvn/Update/VersionUtil.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Produces: `public static class VersionUtil { public static string Current { get; } public static string Normalize(string tag); public static bool IsNewer(string candidate, string current); public static bool IsPrerelease(string tag); }`

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData("v1.0.2", "1.0.2")]
[InlineData("1.0.2", "1.0.2")]
[InlineData("1.0.2+abc123", "1.0.2")]
[InlineData("v2.1.0-beta.1", "2.1.0-beta.1")]
public void Normalize_tira_prefixo_v_e_metadata(string tag, string esperado) =>
    Assert.Equal(esperado, VersionUtil.Normalize(tag));

[Theory]
[InlineData("1.0.2", "1.0.1", true)]
[InlineData("1.0.10", "1.0.9", true)]
[InlineData("1.0.1", "1.0.1", false)]
[InlineData("1.0.0", "1.0.1", false)]
[InlineData("1.0", "1.0.0", false)]
public void IsNewer_compara_por_componente_numerico(string candidato, string atual, bool esperado) =>
    Assert.Equal(esperado, VersionUtil.IsNewer(candidato, atual));

[Fact]
public void IsPrerelease_reconhece_sufixo() =>
    Assert.True(VersionUtil.IsPrerelease("v2.1.0-beta.1"));

[Fact]
public void Current_tem_tres_componentes() =>
    Assert.Matches(@"^\d+\.\d+\.\d+$", VersionUtil.Current);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/GitRemoteSvn.Tests --filter "FullyQualifiedName~VersionUtil|FullyQualifiedName~Normalize|FullyQualifiedName~IsNewer|FullyQualifiedName~IsPrerelease|FullyQualifiedName~Current_tem"`
Expected: FAIL (tipo `VersionUtil` não existe).

- [ ] **Step 3: Implement `VersionUtil`**

`Current` = `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`, cortado no `+`, com fallback `1.0.0`. `Normalize` remove `v` inicial e tudo após `+`. `IsNewer` compara componente a componente numérica (até 4, faltante = 0) sobre a parte antes de `-`; se igual e o candidato é pré-release, não é mais novo. `IsPrerelease` = contém `-` após normalizar.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/GitRemoteSvn.Tests --filter "FullyQualifiedName~UpdateTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GitRemoteSvn/Update/VersionUtil.cs tests/GitRemoteSvn.Tests/UpdateTests.cs
git commit -m "feat: comparacao de versao para o self-update"
```

---

### Task 2: Cache da checagem (`UpdateCache`)

**Files:**
- Create: `src/GitRemoteSvn/Update/UpdateCache.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Produces: `public sealed record UpdateCacheEntry(DateTimeOffset CheckedAtUtc, string? LatestVersion, string? AssetName);`
  `public sealed class UpdateCache { public UpdateCache(string path); public static string DefaultPath { get; } public UpdateCacheEntry? Read(); public bool IsFresh(UpdateCacheEntry entry, TimeSpan ttl); public void Write(UpdateCacheEntry entry); }`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Cache_roundtrip_e_ttl()
{
    var path = Path.Combine(Path.GetTempPath(), "grs-cache-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var cache = new UpdateCache(path);
        Assert.Null(cache.Read());
        var agora = DateTimeOffset.UtcNow;
        cache.Write(new UpdateCacheEntry(agora, "1.0.2", null));
        var lido = cache.Read();
        Assert.Equal("1.0.2", lido!.LatestVersion);
        Assert.True(cache.IsFresh(lido, TimeSpan.FromHours(24)));
        Assert.False(cache.IsFresh(lido, TimeSpan.Zero));
    }
    finally { File.Delete(path); }
}

[Fact]
public void Cache_invalido_e_lixo_devolve_null()
{
    var path = Path.Combine(Path.GetTempPath(), "grs-cache-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, "{ nao e json");
    try { Assert.Null(new UpdateCache(path).Read()); }
    finally { File.Delete(path); }
}

[Fact]
public void DefaultPath_fica_sob_diretorio_do_usuario() =>
    Assert.Contains("git-remote-svn", UpdateCache.DefaultPath);
```

- [ ] **Step 2: Run test to verify it fails** — `dotnet test --filter "FullyQualifiedName~Cache_"` → FAIL (tipo inexistente).

- [ ] **Step 3: Implement `UpdateCache`**

`DefaultPath`: Windows `%LOCALAPPDATA%\git-remote-svn\update.json`; senão `$XDG_CACHE_HOME/git-remote-svn/update.json` ou `~/.cache/git-remote-svn/update.json`. `Read` engole `JsonException`/`IOException` e devolve `null`; tolera data no futuro (tratada como não fresca). `Write` cria diretório, grava `.tmp` e `File.Move(tmp, path, true)`, engolindo falhas de I/O (o cache é opcional).

- [ ] **Step 4: Run test to verify it passes** — PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat: cache diario da checagem de atualizacao"`.

---

### Task 3: Opções (`UpdateOptions`)

**Files:**
- Create: `src/GitRemoteSvn/Update/UpdateOptions.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Produces: `public sealed record UpdateOptions(string Repo, string ApiBase, bool Quiet)` com
  `public static UpdateOptions FromEnvironment(Func<string, string?>? getEnv = null)` e
  `public static bool AutoUpdateRequested(Func<string, string?>? getEnv = null)`.
  Constantes: repo padrão `WenD3v/git-remote-svn`, API padrão `https://api.github.com`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Options_le_ambiente_e_usa_padroes()
{
    var padrao = UpdateOptions.FromEnvironment(_ => null);
    Assert.Equal("WenD3v/git-remote-svn", padrao.Repo);
    Assert.Equal("https://api.github.com", padrao.ApiBase);
    Assert.False(padrao.Quiet);

    var custom = UpdateOptions.FromEnvironment(k => k switch
    {
        "GIT_REMOTE_SVN_UPDATE_REPO" => "eu/outro",
        "GIT_REMOTE_SVN_UPDATE_API" => "https://espelho/api",
        "GIT_REMOTE_SVN_QUIET" => "1",
        _ => null,
    });
    Assert.Equal("eu/outro", custom.Repo);
    Assert.Equal("https://espelho/api", custom.ApiBase);
    Assert.True(custom.Quiet);
}

[Theory]
[InlineData("1", true)]
[InlineData("true", true)]
[InlineData("0", false)]
[InlineData(null, false)]
public void AutoUpdate_vem_do_ambiente(string? valor, bool esperado) =>
    Assert.Equal(esperado, UpdateOptions.AutoUpdateRequested(_ => valor));
```

- [ ] **Step 2: Run test to verify it fails** — FAIL.

- [ ] **Step 3: Implement `UpdateOptions`**

`FromEnvironment` corta `/` final do `ApiBase`; `Quiet` = env `1`/`true` (case-insensitive). `AutoUpdateRequested` aceita `1`/`true`. A checagem de `git config --global --get remote-svn.autoUpdate` fica no `Program.cs` (Task 6), que só a consulta quando o cache venceu.

- [ ] **Step 4: Run test to verify it passes** — PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat: opcoes de atualizacao por ambiente"`.

---

### Task 4: Consulta do release (`UpdateService.CheckAsync`)

**Files:**
- Create: `src/GitRemoteSvn/Update/UpdateService.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Produces:
  `public sealed record ReleaseAsset(string Name, string DownloadUrl);`
  `public sealed record ReleaseInfo(string Tag, string Version, IReadOnlyList<ReleaseAsset> Assets);`
  `public sealed class UpdateService(UpdateOptions options, HttpClient http, string exePath, TextWriter? stderr = null)`:
  `public Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)` (null = rate limit/sem release/rede);
  `public static ReleaseAsset? SelectAsset(IEnumerable<ReleaseAsset> assets, string os, string arch)`;
  `public static string? ParseSums(string text, string assetName)` (hash em minúsculas ou null).

- [ ] **Step 1: Write the failing test** (handler falso, sem rede)

```csharp
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
        Task.FromResult(responder(r));
}

[Fact]
public async Task Check_le_tag_assets_e_ignora_prerelease()
{
    var json = """
    {"tag_name":"v1.0.2","prerelease":false,"assets":[
      {"name":"git-remote-svn-v1.0.2-win-x64.zip","browser_download_url":"https://x/win.zip"},
      {"name":"SHA256SUMS","browser_download_url":"https://x/SHA256SUMS"}]}
    """;
    var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
    var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null), http, "git-remote-svn.exe");
    var rel = await svc.CheckAsync();
    Assert.Equal("1.0.2", rel!.Version);
    Assert.Equal(2, rel.Assets.Count);
}

[Fact]
public async Task Check_devolve_null_em_falha_ou_rate_limit()
{
    var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null), http, "git-remote-svn.exe");
    Assert.Null(await svc.CheckAsync());
}

[Theory]
[InlineData("win-x64", "git-remote-svn-v1.0.2-win-x64.zip")]
[InlineData("linux-x64", "git-remote-svn-v1.0.2-linux-x64.tar.gz")]
public void SelectAsset_escolhe_pelo_sufixo(string rid, string esperado)
{
    var assets = new[]
    {
        new ReleaseAsset("git-remote-svn-v1.0.2-win-x64.zip", "a"),
        new ReleaseAsset("git-remote-svn-v1.0.2-linux-x64.tar.gz", "b"),
        new ReleaseAsset("SHA256SUMS", "c"),
    };
    Assert.Equal(esperado, UpdateService.SelectAsset(assets, rid.Split('-')[0], rid.Split('-')[1])!.Name);
}

[Fact]
public void ParseSums_encontra_a_linha_do_asset()
{
    var text = "abc123  outra.zip\nDEF456  git-remote-svn-v1.0.2-win-x64.zip\n";
    Assert.Equal("def456", UpdateService.ParseSums(text, "git-remote-svn-v1.0.2-win-x64.zip"));
    Assert.Null(UpdateService.ParseSums(text, "nao-existe.zip"));
}
```

- [ ] **Step 2: Run test to verify it fails** — FAIL.

- [ ] **Step 3: Implement `UpdateService.CheckAsync`**

`GET {ApiBase}/repos/{Repo}/releases/latest` com headers `User-Agent: git-remote-svn/{VersionUtil.Current}` e `Accept: application/vnd.github+json`; timeout de 5s via `CancellationTokenSource`. Parse com `JsonDocument`: `tag_name`, `prerelease` (se `true`, devolve `null`), `assets[].name`/`browser_download_url`. Status != 200, exceção de rede ou JSON inválido → `null` (sem log). `SelectAsset` aceita nome terminado em `-{os}-{arch}.zip`/`.tar.gz`, ignorando `SHA256SUMS`.

- [ ] **Step 4: Run test to verify it passes** — PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat: consulta do release mais recente"`.

---

### Task 5: Instalação (`UpdateService.UpdateAsync`)

**Files:**
- Modify: `src/GitRemoteSvn/Update/UpdateService.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Produces: `public sealed record UpdateResult(bool Updated, string? Version, string? Message);`
  `public Task<UpdateResult> UpdateAsync(ReleaseInfo release, CancellationToken ct = default)`;
  `public static Task<byte[]> DownloadAsync(HttpClient http, string url, CancellationToken ct)` (interno, usado pelo teste).

- [ ] **Step 1: Write the failing test**

```csharp
static byte[] ZipCom(string nomeInterno, byte[] conteudo) { /* ZipArchive em MemoryStream */ }

[Fact]
public async Task Update_recusa_sem_sha256sums()
{
    var dir = Directory.CreateTempSubdirectory("grs-up").FullName;
    var exe = Path.Combine(dir, "git-remote-svn.exe");
    File.WriteAllBytes(exe, "antigo"u8.ToArray());
    var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[] { new ReleaseAsset("git-remote-svn-v9.9.9-win-x64.zip", "https://x/a.zip") });
    var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null), new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), exe);
    var r = await svc.UpdateAsync(rel);
    Assert.False(r.Updated);
    Assert.Contains("SHA256SUMS", r.Message);
    Assert.Equal("antigo", File.ReadAllText(exe));
}

[Fact]
public async Task Update_recusa_hash_divergente()
{
    // release com zip valido + SHA256SUMS com hash "0000..."; exe intacto
}

[Fact]
public async Task Update_troca_o_executavel_quando_hash_confere()
{
    // zip contendo "git-remote-svn.exe" com bytes "novo"; SHA256SUMS correto
    // Assert.True(r.Updated); Assert.Equal("novo", File.ReadAllText(exe));
    // Assert.Equal("novo", File.ReadAllText(exe + ".old"));  // original preservado
}

[Fact]
public async Task Update_extrai_tar_gz()
{
    // mesma coisa com .tar.gz (TarWriter + GZipStream), exePath "git-remote-svn"
}
```

- [ ] **Step 2: Run test to verify it fails** — FAIL.

- [ ] **Step 3: Implement `UpdateAsync`**

Ordem: (1) recusa se `Environment.ProcessPath` for `dotnet`/`dotnet.exe`; (2) `SelectAsset` para `RuntimeInformation.RuntimeIdentifier` (usa `os`/`arch` do processo) — sem asset → `UpdateResult(false, null, "sem atualizacao para esta plataforma")`; (3) baixa o asset e o `SHA256SUMS` do mesmo release; (4) `ParseSums`, confere `SHA256` do arquivo baixado (divergente/ausente → recusa com mensagem); (5) extrai em diretório temporário (`zip`: `ZipFile.ExtractToDirectory`; `tar.gz`: `TarReader`), acha `git-remote-svn.exe`/`git-remote-svn`; (6) instala: Windows — apaga `.old` antigo, `File.Move(exe, exe + ".old")`, copia o novo para `exe`, em falha restaura o `.old`; Linux — grava `exe + ".new"`, `File.SetUnixFileMode(0755)`, `File.Move(.new, exe, true)`; (7) `UpdateResult(true, release.Version, null)`.

- [ ] **Step 4: Run test to verify it passes** — PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat: baixa, verifica e troca o executavel"`.

---

### Task 6: Comandos e checagem automática (`Program.cs`)

**Files:**
- Modify: `src/GitRemoteSvn/Program.cs`
- Test: `tests/GitRemoteSvn.Tests/UpdateTests.cs`

**Interfaces:**
- Consumes: `VersionUtil`, `UpdateOptions`, `UpdateCache`, `UpdateService`.
- Produces: comportamento de linha de comando: `--version`/`version` (sai 0, imprime `git-remote-svn <versao> (<rid>)`), `update` (aplica), `update --check` (só informa), `update --force` (reinstala), `--quiet`.

- [ ] **Step 1: Write the failing test** (usa o binário publicado pelo fixture)

```csharp
[Fact]
public async Task Version_imprime_versao_e_rid()
{
    var dir = await HelperBinary.GetDirAsync();
    var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "git-remote-svn.exe" : "git-remote-svn");
    var (code, stdout, _) = await RunAsync(exe, "--version");
    Assert.Equal(0, code);
    Assert.StartsWith("git-remote-svn ", stdout);
    Assert.Matches(@"\d+\.\d+\.\d+", stdout);
}

[Fact]
public async Task Update_check_sem_rede_nao_falha()
{
    // GIT_REMOTE_SVN_UPDATE_API=http://127.0.0.1:9  (porta fechada)
    // update --check => exit 0 e mensagem "sem informacao"/"nao foi possivel"
}
```

- [ ] **Step 2: Run test to verify it fails** — FAIL (hoje `--version` vira URL inválida e sai 1).

- [ ] **Step 3: Implement no `Program.cs`**

Antes do bloco do protocolo: `if (args.Length == 1 && (args[0] is "--version" or "version"))` imprime e retorna 0; `if (args.Length >= 1 && args[0] == "update")` chama `RunUpdateAsync(args)`. `update --check` consulta e imprime `vX disponivel` / `ja esta na ultima versao`; `update` sem release novo diz o mesmo; falha de rede ou ausência de `SHA256SUMS` → mensagem e código 1. No fim de uma sessão normal, depois de `helper.RunAsync`, chama `NoticeOrAutoUpdateAsync()`: lê `UpdateCache`, se fresco só compara; senão consulta, grava cache e, havendo versão nova, imprime uma linha (`git-remote-svn: nova versao vX disponivel (git-remote-svn update)`) ou, com `remote-svn.autoUpdate=true`/env, aplica e imprime `atualizado para vX`. Qualquer exceção é engolida; o cache é gravado antes do aviso.

- [ ] **Step 4: Run test to verify it passes** — PASS.

- [ ] **Step 5: Commit** — `git commit -m "feat: comandos --version e update com checagem diaria"`.

---

### Task 7: `SHA256SUMS` no release, docs e 1.0.2

**Files:**
- Modify: `.github/workflows/release.yml`, `README.md`, `README.pt-BR.md`, `docs/design.md`, `src/GitRemoteSvn/GitRemoteSvn.csproj`

**Interfaces:** nenhuma de código; o contrato é o asset `SHA256SUMS` (linhas `<sha256>  <nome-do-arquivo>`).

- [ ] **Step 1: Workflow** — no job `release`, após `download-artifact`, gerar `sha256sum git-remote-svn-* > SHA256SUMS` e incluir `SHA256SUMS` no `gh release create`.
- [ ] **Step 2: README (en/pt-BR)** — seção "Updating"/"Atualizando": `git-remote-svn update`, `--check`, `remote-svn.autoUpdate`, e comportamento do Windows (a sessão atual continua na versão antiga).
- [ ] **Step 3: docs/design.md** — parágrafo em "Distribution"/"Big picture" sobre o canal de atualização e o `SHA256SUMS`.
- [ ] **Step 4: versão 1.0.2** — `<Version>1.0.2</Version>`.
- [ ] **Step 5: suíte completa** — `dotnet test -c Release` → 75 + novos verdes.
- [ ] **Step 6: Commit** — `git commit -m "docs: canal de atualizacao e SHA256SUMS no release"`.

---

## Validação final (fora dos commits)

1. `dotnet publish src/GitRemoteSvn -c Release -r win-x64 -o dist/win-x64` e rodar `git-remote-svn --version` / `--check` com o release real (v1.0.2 já publicado).
2. Publicar `v1.0.2` (tag), conferir o `SHA256SUMS` no release e rodar `git-remote-svn update` numa cópia instalada apontando para o repo público.

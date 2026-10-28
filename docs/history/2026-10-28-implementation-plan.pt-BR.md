> **Documento histórico** (o desenho e o plano usados na primeira versão, com nomes de servidor e de projetos trocados por exemplos). O comportamento atual está em [docs/design.md](../design.md).

# git-remote-svn Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Um executável `git-remote-svn` (Windows/Linux) que faz `git clone/fetch/pull/push` funcionarem contra um projeto Subversion, com `origin` = URL do SVN, sem nunca sobrescrever trabalho.

**Architecture:** Remote helper do Git (protocolo stdin/stdout) em C#/.NET 9. Importação de histórico via `svn log/list/cat` + `git fast-import`; exportação via working copy SVN temporária em `.git/svn/wc` + `svn commit`, uma revisão por commit e uma por merge. Estado e mapeamento commit↔revisão em `.git/svn/` + trailer `svn-id`.

**Tech Stack:** C# / .NET 9.0.318 (já instalado), xUnit, `git` 2.55, `svn` 1.14.5. Sem dependências NuGet além de xUnit/test SDK.

**Spec:** `docs/superpowers/specs/2026-10-28-git-remote-svn-design.md`

## Global Constraints

- Projeto em `git-remote-svn`; solução `git-remote-svn.sln`, app em `src/GitRemoteSvn`, testes em `tests/GitRemoteSvn.Tests`.
- `AssemblyName` = `git-remote-svn`; no Windows o binário final é `git-remote-svn.exe`, no Linux `git-remote-svn`.
- Toda comunicação com SVN pelo executável `svn` (XML em `log`, `list`, `info`); toda comunicação com o repositório pelo executável `git`. Nenhuma biblioteca nativa de SVN.
- Trailer obrigatório nos commits importados/espelho: `svn-id: <url-absoluta>@<rev> <uuid-do-repo>`.
- Clones criados pelo helper configuram `core.autocrlf=false` e `branch.<nome>.rebase=true`.
- Push: nunca `--force`, nunca resolução automática de conflito, nunca revisão vazia; desatualizado ⇒ recusar com instrução de `git pull`.
- Testes automatizados usam **somente** repositório SVN local descartável (`svnadmin create` + `file://`). Nenhum teste escreve em `svn.example.com`.
- Validação no servidor real (Task 15): leitura livre em `projetos/web/vendas`; **escrita somente em `projetos/web/vendas/branches/git-remote-svn-teste-*` e somente após confirmação explícita do usuário na hora**; `trunk` real nunca recebe commit de teste.
- Commits em português, mensagens curtas (`feat:`, `test:`, `chore:`).

## Review Focus

1. **Binários e CRLF**: arquivo `.res`/`.png` e texto CRLF devem chegar byte a byte idênticos ao `svn export` (clone e push) — teste na Task 5 e Task 9.
2. **Revisão que toca vários projetos**: só os caminhos do projeto clonado entram no commit — teste na Task 5.
3. **Caminhos com acento, espaço e >260 caracteres no Windows**: clone e push sem quebra — teste na Task 5 (clone) e Task 9 (push).
4. **Push interrompido no meio**: reexecutar não pode duplicar revisões — teste na Task 13.
5. **`svn:ignore` e pastas vazias**: ignore vai para `.git/info/exclude` sem criar `.gitignore` no SVN; pasta vazia não vira commit — teste na Task 5.

---

### Task 1: Esqueleto da solução + fixture de repositório SVN local

**Files:**
- Create: `git-remote-svn.sln`, `.gitignore` (`bin/`, `obj/`, `dist/`, `TestResults/`, `.vs/`)
- Create: `src/GitRemoteSvn/GitRemoteSvn.csproj` (`net9.0`, `OutputType=Exe`, `AssemblyName=git-remote-svn`, `Nullable=enable`, `ImplicitUsings=enable`)
- Create: `src/GitRemoteSvn/Program.cs` (por ora: `Console.WriteLine("git-remote-svn");`)
- Create: `tests/GitRemoteSvn.Tests/GitRemoteSvn.Tests.csproj` (xUnit, referência ao app)
- Create: `tests/GitRemoteSvn.Tests/Fixtures/SvnTestRepo.cs`
- Test: `tests/GitRemoteSvn.Tests/Fixtures/SvnTestRepoTests.cs`

**Interfaces:**
- Produces: `SvnTestRepo : IAsyncDisposable` com `string RepoUrl` (file://...), `string WcPath`, `static Task<SvnTestRepo> CreateAsync()` (cria repo + WC com `trunk`, `branches`, `tags`), `Task<long> CommitFileAsync(string relativePath, byte[] content, string message, string author = "alice")` (escreve na WC do trunk, `svn add` se novo, `svn commit --username`), `Task<long> CommitAsync(string message)`, `Task<string> CatAsync(string url)`, `void RunSvn(string args)` e `void RunGit(string args, string cwd)` internos.

- [ ] **Step 1: Teste que falha** — `SvnTestRepoTests.Fixture_cria_layout_e_commita`: cria fixture, `CommitFileAsync("ola.txt", "oi"u8, "primeiro")` retorna 2 (r1 = layout), `CatAsync(RepoUrl + "/trunk/ola.txt")` == "oi".
- [ ] **Step 2: Rodar e ver falhar** — `dotnet test --filter SvnTestRepoTests` ⇒ erro de tipo inexistente.
- [ ] **Step 3: Implementar** `SvnTestRepo` usando `Path.GetTempPath()` + `Guid.NewGuid("N")`, `svnadmin create`, `svn mkdir` do layout com commit r1, `svn checkout` do trunk em `WcPath`; processos com `ProcessStartInfo.ArgumentList`, `RedirectStandardOutput/Error`, falha ⇒ exceção com stderr.
- [ ] **Step 4: Rodar e ver passar** — `dotnet test --filter SvnTestRepoTests` ⇒ PASS.
- [ ] **Step 5: Inicializar git e commitar** — `git init -b main` em `git-remote-svn`; `git add .`; `git commit -m "chore: esqueleto da solucao e fixture svn local"`.

### Task 2: SvnClient (info, list, log, cat)

**Files:**
- Create: `src/GitRemoteSvn/Svn/SvnModels.cs`
- Create: `src/GitRemoteSvn/Svn/SvnClient.cs`
- Test: `tests/GitRemoteSvn.Tests/SvnClientTests.cs`

**Interfaces:**
- Produces:
  - `record SvnEntry(string Name, string Kind, long Revision);`
  - `record SvnPathChange(char Action, string Path, string? CopyFromPath, long? CopyFromRev, string Kind);`
  - `record SvnRevision(long Revision, string Author, DateTimeOffset Date, string Message, IReadOnlyList<SvnPathChange> Paths);`
  - `record SvnInfo(string Url, string RepoRootUrl, string Uuid, long Revision);`
  - `class SvnClient(string svnExe = "svn")` com `Task<SvnInfo> InfoAsync(string url)`, `Task<IReadOnlyList<SvnEntry>> ListAsync(string url)`, `Task<IReadOnlyList<SvnRevision>> LogAsync(string url, long startRev, long endRev, bool stopOnCopy)`, `Task<byte[]> CatAsync(string urlAtRev)` (suporta `url@rev`), `Task<string> RunAsync(IEnumerable<string> args)` (XML ou texto, stderr ⇒ `SvnException`).

- [ ] **Step 1: Testes que falham** — `Info_retorna_url_uuid_e_revisao`; `Log_retorna_autor_data_mensagem_e_paths` (após dois commits, o segundo com `M` em `ola.txt`; datas em UTC, autor "alice"); `Log_com_copyfrom_expoe_origem` (após `svn copy trunk branches/b1`, a revisão tem `CopyFromPath` terminando em `/trunk`); `Cat_retorna_bytes_exatos` (conteúdo com bytes 0x00–0xFF).
- [ ] **Step 2:** `dotnet test --filter SvnClientTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — parsing com `XDocument`; `log --xml -v [-r start:end] [--stop-on-copy]`; datas `DateTimeOffset.Parse` com `CultureInfo.InvariantCulture`; mensagem pode ser vazia.
- [ ] **Step 4:** `dotnet test --filter SvnClientTests` ⇒ PASS.
- [ ] **Step 5:** `git add .; git commit -m "feat: cliente svn com info, list, log e cat"`.

### Task 3: Layout do projeto + mapa de autores + trailer svn-id

**Files:**
- Create: `src/GitRemoteSvn/Sync/ProjectLayout.cs`
- Create: `src/GitRemoteSvn/Sync/SvnId.cs`
- Create: `src/GitRemoteSvn/Sync/AuthorMap.cs`
- Test: `tests/GitRemoteSvn.Tests/LayoutTests.cs`, `tests/GitRemoteSvn.Tests/SvnIdTests.cs`

**Interfaces:**
- Produces:
  - `record ProjectLayout(string RootUrl, string TrunkUrl, bool Standard, IReadOnlyList<string> Branches, IReadOnlyList<string> Tags);` + `static Task<ProjectLayout> DiscoverAsync(SvnClient svn, string url)` (se existir `trunk`, usa `trunk/branches/tags`; senão `Standard=false` e a própria URL é o tronco).
  - `static class SvnId` com `string Format(string url, long rev, string uuid)` e `bool TryParse(string message, out string url, out long rev)` (lê a última linha `svn-id:` do corpo).
  - `class AuthorMap` com `static AuthorMap Load(string? path)` e `(string Name, string Email) Resolve(string svnUser)` (padrão `svnUser` / `svnUser@svn.local`; arquivo `usuario = Nome <email>`).

- [ ] **Step 1: Testes que falham** — `Discover_com_trunk_branches_tags` (fixture + `svn copy trunk branches/b1` + tag `t1` ⇒ `Standard=true`, `Branches=["b1"]`, `Tags=["t1"]`); `Discover_pasta_simples` (URL de `trunk` diretamente ⇒ `Standard=false`); `SvnId_roundtrip_e_parse_da_ultima_linha`; `AuthorMap_padrao_e_arquivo`.
- [ ] **Step 2:** `dotnet test --filter "LayoutTests|SvnIdTests"` ⇒ FAIL.
- [ ] **Step 3: Implementar** (usa `SvnClient.ListAsync`/`InfoAsync` da Task 2).
- [ ] **Step 4:** mesmos filtros ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: descoberta de layout, autores e trailer svn-id"`.

### Task 4: GitClient + FastImportWriter

**Files:**
- Create: `src/GitRemoteSvn/Git/GitClient.cs`
- Create: `src/GitRemoteSvn/Git/FastImportWriter.cs`
- Test: `tests/GitRemoteSvn.Tests/FastImportTests.cs`

**Interfaces:**
- Produces:
  - `class GitClient(string repoPath, string gitExe = "git")` com `Task<string> RunAsync(params string[] args)`, `Task ConfigSetAsync(string key, string value)`, `Task<string?> ConfigGetAsync(string key)`, `Task<string> RevParseAsync(string refName)`, `Task<bool> RefExistsAsync(string refName)`, `Task UpdateRefAsync(string refName, string sha)`, `Task<FastImportWriter> BeginFastImportAsync()`, `string GitDir` (resolve `git rev-parse --absolute-git-dir`).
  - `class FastImportWriter : IAsyncDisposable` com `Task CommitAsync(string refName, string? fromRef, (string Name, string Email, DateTimeOffset Date) author, string message, IReadOnlyList<ImportFile> files)`, `record ImportFile(string Path, byte[]? Content)` (`Content=null` ⇒ `D <path>`), `Task MarkAsync(string refName, string sha)`.
  - Escreve `commit`, `from`, `author`, `committer`, `data <n>` + bytes, `M 100644 inline <path>` / `D <path>`, `done` no dispose.

- [ ] **Step 1: Teste que falha** — `FastImport_cria_commit_com_autor_data_mensagem_e_arquivos`: em repo vazio (`git init`), importa 2 arquivos (um binário) na ref `refs/heads/main`; `git log --format=%an|%ae|%s` bate; `git show HEAD:bin.dat` tem os mesmos bytes; segundo commit com `D` remove o arquivo.
- [ ] **Step 2:** `dotnet test --filter FastImportTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — stream de stdin do `git fast-import --quiet`; sempre `data <len>` (nunca delimitador); autor e committer iguais aos da revisão.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: escrita de historico via git fast-import"`.

### Task 5: RevisionImporter — clone completo do tronco

**Files:**
- Create: `src/GitRemoteSvn/Sync/RevisionImporter.cs`
- Create: `src/GitRemoteSvn/Sync/StateStore.cs`
- Test: `tests/GitRemoteSvn.Tests/RevisionImporterTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 3, 4.
- Produces:
  - `class StateStore(string gitDir)` com `string Root` = `<gitDir>/svn`, `long? GetLastRev(string refName)`, `void SetLastRev(string refName, long rev)`, `void SaveLayout(ProjectLayout)` / `ProjectLayout? LoadLayout()` (JSON simples em `<gitDir>/svn/state.json`).
  - `class RevisionImporter(SvnClient svn, GitClient git, StateStore state, AuthorMap authors)` com `Task<int> ImportTrunkAsync(ProjectLayout layout, string targetRef = "refs/heads/main")` — retorna nº de commits criados; importa de `1..HEAD` com `stopOnCopy: state já tem rev ? false : true`; grava mensagem original + `\n\n` + `SvnId.Format(...)`; filtra `Paths` para os que começam com o caminho do tronco; aplica `svn:ignore` do tronco em `.git/info/exclude`; configura `core.autocrlf=false` e `branch.main.rebase=true`.

- [ ] **Step 1: Testes que falham** (todos contra fixture):
  - `Clone_importa_todas_as_revisoes_com_metadados` (3 commits ⇒ 3 commits Git, mensagens com `svn-id`, autores `alice <alice@svn.local>`).
  - `Clone_preserva_binario_crlf_e_nomes_dificeis` (arquivo com bytes 0x00–0xFF, texto CRLF, `pasta com espaço/ação.txt`, caminho aninhado com 280 chars) ⇒ `git show` igual ao original e igual a `svn export`.
  - `Clone_ignora_caminhos_de_outros_projetos_na_mesma_revisao` (revisão com `M` no trunk e `A` fora do tronco, ex.: `branches/outro/x.txt`) ⇒ commit não contém `x.txt`.
  - `Clone_aplica_svn_ignore_sem_criar_gitignore` e `Clone_nao_cria_commit_para_pasta_vazia`.
  - `Clone_e_idempotente` (rodar 2× ⇒ segunda retorna 0).
- [ ] **Step 2:** `dotnet test --filter RevisionImporterTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — obter conteúdo por `CatAsync($"{path}@{rev}")`; mudanças `A`/`M`/`R` ⇒ escrever bytes, `D` ⇒ delete (inclusive prefixo de diretório); revisão sem nenhum caminho do tronco ⇒ não cria commit mas avança `lastRev`.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: importacao do historico do tronco com binarios e ignore"`.

### Task 6: Remote helper — `git clone svn::...` de ponta a ponta

**Files:**
- Create: `src/GitRemoteSvn/Helper/RemoteHelper.cs`
- Modify: `src/GitRemoteSvn/Program.cs`
- Test: `tests/GitRemoteSvn.Tests/RemoteHelperCloneTests.cs`

**Interfaces:**
- Consumes: Tasks 2–5.
- Produces: `class RemoteHelper(SvnClient svn, AuthorMap authors)` com `Task RunAsync(string url, TextReader input, TextWriter output)` respondendo `capabilities` (`fetch`, `push`, `option`), `option`, `list` (por ora só `HEAD`/`main`), `fetch <sha> <ref>`, linha vazia ⇒ sair. `Program.cs`: `args = [url]`; remove prefixo `svn::`; monta `GitClient` no repo atual; despacha para `RemoteHelper`; erros ⇒ `Console.Error` + exit 1.

- [ ] **Step 1: Teste que falha (integração real)** — publica o app (`dotnet publish -c Release`) em pasta temporária, prepend ao PATH e roda `git clone "svn::<RepoUrl>" dest` via `Process`; asserta `dest/.git` existe, `git -C dest log --oneline` tem os mesmos commits do SVN, working tree tem os arquivos, `git -C dest config core.autocrlf` == `false`, `branch.main.rebase` == `true`.
- [ ] **Step 2:** `dotnet test --filter RemoteHelperCloneTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — durante `fetch`, usar `StateStore`/`RevisionImporter` no repo destino; responder a lista de refs a partir do estado importado; em clone vazio, importar antes do primeiro `list` se necessário (cache por processo).
- [ ] **Step 4:** ⇒ PASS, e manualmente `git clone svn::<fixture>` funciona num terminal.
- [ ] **Step 5:** `git commit -m "feat: git clone svn:: funcional via remote helper"`.

### Task 7: Branches e tags no clone e no fetch

**Files:**
- Modify: `src/GitRemoteSvn/Sync/RevisionImporter.cs` (novo método)
- Modify: `src/GitRemoteSvn/Helper/RemoteHelper.cs` (`list` completo)
- Test: `tests/GitRemoteSvn.Tests/BranchTagTests.cs`

**Interfaces:**
- Produces: `Task ImportBranchesAndTagsAsync(ProjectLayout layout)` — cria `refs/remotes/origin/<branch>` e `refs/tags/<tag>`; branch criada por `svn copy` aponta para o commit de origem já importado (via `CopyFromRev` + `SvnId`), importando só o delta; deleção no SVN remove a ref remota e avisa no stderr; `list` devolve HEAD simbólico + todas as refs.

- [ ] **Step 1: Testes que falham** — `Clone_traz_branches_e_tags` (fixture com `branches/b1` com 1 commit próprio e `tags/t1`); `Branch_criada_por_copia_nao_duplica_historico` (ancestral comum é o commit do trunk); `Branch_apagada_no_svn_some_no_fetch`; `Tag_modificada_no_svn_nao_move_tag_local_e_avisa`; `Fetch_incremental_traz_so_revisoes_novas` (após novo commit no SVN, `git fetch` adiciona exatamente 1 commit e não altera working tree nem branch local).
- [ ] **Step 2:** `dotnet test --filter BranchTagTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** (usa `LogAsync(..., stopOnCopy: true)` por branch/tag e o mapa `svn-id` já importado).
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: branches e tags do svn no clone e no fetch"`.

### Task 8: Working copy SVN temporária

**Files:**
- Create: `src/GitRemoteSvn/Sync/SvnWorkingCopy.cs`
- Test: `tests/GitRemoteSvn.Tests/SvnWorkingCopyTests.cs`

**Interfaces:**
- Produces: `class SvnWorkingCopy(SvnClient svn, string rootDir)` com `Task<string> EnsureAsync(string branchUrl, long rev)` (checkout ou `svn update -r rev`, devolve o caminho), `Task ApplyTreeAsync(string wcPath, IReadOnlyList<ImportFile> changes)` (escreve bytes, `svn add` com `--parents`, `svn delete`, cria/apaga pastas), `Task<long> CommitAsync(string wcPath, string message)` (faz parse de `Committed revision N.`), `Task SetMergeInfoAsync(string wcPath, string value)`, `Task RevertAsync(string wcPath)` (`svn revert -R` + limpar não versionados).

- [ ] **Step 1: Testes que falham** — `Ensure_faz_checkout_e_update`; `ApplyTree_adiciona_modifica_renomeia_e_apaga` (inclui subpasta nova e nome com acento); `Commit_retorna_numero_da_revisao`; `Commit_sem_alteracao_nao_cria_revisao` (retorna 0 e o chamador trata).
- [ ] **Step 2:** `dotnet test --filter SvnWorkingCopyTests` ⇒ FAIL.
- [ ] **Step 3: Implementar**.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: working copy svn temporaria para push"`.

### Task 9: Push linear — N commits ⇒ N revisões

**Files:**
- Create: `src/GitRemoteSvn/Sync/Pusher.cs`
- Modify: `src/GitRemoteSvn/Helper/RemoteHelper.cs` (`push <ref>:<ref>`)
- Test: `tests/GitRemoteSvn.Tests/PushLinearTests.cs`

**Interfaces:**
- Consumes: Tasks 4, 5, 8.
- Produces: `record PushResult(int RevisionsCreated, int EmptyCommitsSkipped, IReadOnlyList<string> Warnings);` e `class Pusher(SvnClient svn, GitClient git, SvnWorkingCopy wc, StateStore state)` com `Task<PushResult> PushBranchAsync(string localRef, string branchUrl, string remoteRef, bool dryRun)`. Algoritmo: `git rev-list --first-parent --reverse <remote>..<local>`; para cada commit sem `svn-id`, diff contra o primeiro pai (`git diff-tree --no-commit-id -r -M <sha>`), aplicar na WC e commitar com a mensagem original (sem trailer); depois criar commit espelho com `svn-id` via fast-import e atualizar `remoteRef` e a branch local (rebase interno).

- [ ] **Step 1: Testes que falham** — `Push_de_3_commits_cria_3_revisoes_com_mensagens` (`svn log` no SVN mostra as 3 mensagens, na ordem); `Conteudo_no_svn_fica_igual_ao_git` (diff `svn export` × `git archive`, incluindo binário, CRLF, caminho com acento/espaço/longo); `Push_atualiza_branch_local_para_commits_com_svn_id` (`git log` local tem `svn-id` e `git status` limpo); `Push_vazio_nao_faz_nada`; `Dry_run_nao_escreve_no_servidor`.
- [ ] **Step 2:** `dotnet test --filter PushLinearTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** (usa `ImportFile` da Task 4 como representação de mudança).
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: push linear com uma revisao por commit"`.

### Task 10: Concorrência — ninguém sobrescreve ninguém

**Files:**
- Modify: `src/GitRemoteSvn/Sync/Pusher.cs`
- Test: `tests/GitRemoteSvn.Tests/ConcurrencyTests.cs`

**Interfaces:**
- Consumes: Tasks 6, 7, 9.

- [ ] **Step 1: Testes que falham** — `Push_com_servidor_avancado_e_recusado_sem_alterar_nada`: clone A, commit local em A, commit independente via `SvnTestRepo` (cliente B); `git -C A push` ⇒ exit != 0, stderr orienta `git pull`, `svn log` mostra só a revisão de B, branch local de A intacta; `Depois_do_pull_rebase_o_push_entra_sem_sobrescrever`: `git -C A pull` (rebase) + `push` ⇒ SVN tem revisão de B **e** a de A, conteúdo final = união; `Conflito_de_conteudo_para_no_rebase_e_preserva_trabalho`: mesmo arquivo/mesma linha ⇒ `git pull` retorna conflito, arquivos locais com marcadores, nada commitado no SVN.
- [ ] **Step 2:** `dotnet test --filter ConcurrencyTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — pré-condição no push: `svn info` da branch comparada à `lastRev` da ref remota; divergência ⇒ fetch automático + erro `non-fast-forward` no protocolo (o Git mostra "fetch first"); tratar `out of date` do `svn commit` como abort seguro + `RevertAsync`.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: protecao contra push desatualizado e conflitos"`.

### Task 11: Merge, commits vazios e recusas

**Files:**
- Modify: `src/GitRemoteSvn/Sync/Pusher.cs`
- Test: `tests/GitRemoteSvn.Tests/MergePushTests.cs`

**Interfaces:**
- Consumes: Tasks 7–10; usa `SvnWorkingCopy.SetMergeInfoAsync`.

- [ ] **Step 1: Testes que falham** — `Merge_no_ff_vira_uma_revisao_com_mergeinfo`: branch `b1` publicada com 2 commits, merge `--no-ff` na main, push ⇒ trunk ganha **1** revisão (a do merge, além das 2 da branch), `svn propget svn:mergeinfo <trunkUrl>` contém `/branches/b1:<r1>-<r2>`; `Merge_de_branch_nao_publicada_e_recusado` (mensagem manda `git push -u origin b1`); `Merge_octopus_e_recusado`; `Commit_vazio_e_pulado_com_aviso` (`PushResult.EmptyCommitsSkipped == 1`, nenhuma revisão criada); `Merge_sem_alteracao_nao_cria_revisao`.
- [ ] **Step 2:** `dotnet test --filter MergePushTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — detecção de merge por `git rev-list --parents -n1`; diff do merge = `git diff <primeiro-pai> <merge>`; intervalo de revisões do segundo pai lido dos trailers `svn-id`.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: merge como revisao unica com svn:mergeinfo"`.

### Task 12: Push de branch nova e de tag

**Files:**
- Modify: `src/GitRemoteSvn/Svn/SvnClient.cs` (`CopyAsync`)
- Modify: `src/GitRemoteSvn/Sync/Pusher.cs`
- Test: `tests/GitRemoteSvn.Tests/BranchTagPushTests.cs`

**Interfaces:**
- Produces: `Task SvnClient.CopyAsync(string srcUrlAtRev, string dstUrl, string message)`; em `Pusher`: `Task PushNewBranchAsync(string localRef, string branchUrl)` (cria via `svn copy` da revisão-base e delega a `PushBranchAsync`) e `Task PushTagAsync(string tagName, string sha)`.

- [ ] **Step 1: Testes que falham** — `Push_de_branch_nova_cria_svn_copy_e_commits` (colega SVN faz checkout da branch e vê os arquivos); `Branch_com_barra_no_nome_vira_hierarquia` (`feature/x` ⇒ `branches/feature/x`); `Push_de_tag_cria_copia_e_recusa_tag_existente`; `Tag_de_commit_nao_publicado_e_recusada`; `Colega_svn_cria_branch_e_elas_se_enxergam` (dois clones A e B: branch criada por A aparece no `git ls-remote` de B).
- [ ] **Step 2:** `dotnet test --filter BranchTagPushTests` ⇒ FAIL.
- [ ] **Step 3: Implementar**.
- [ ] **Step 4:** ⇒ PASS.
- [ ] **Step 5:** `git commit -m "feat: push de branches novas e tags via svn copy"`.

### Task 13: Recuperação, logs e dry-run completo

**Files:**
- Create: `src/GitRemoteSvn/Sync/SyncLog.cs`
- Modify: `src/GitRemoteSvn/Sync/Pusher.cs`, `src/GitRemoteSvn/Helper/RemoteHelper.cs`
- Test: `tests/GitRemoteSvn.Tests/RecoveryTests.cs`

**Interfaces:**
- Produces: `SyncLog` escrevendo em `<gitDir>/svn/log/yyyymmdd-hhmmss.log`; `Pusher` com ponto de injeção `Func<int,Task>? AfterEachRevision` para o teste simular falha.

- [ ] **Step 1: Testes que falham** — `Push_interrompido_ao_segundo_commit_nao_duplica_na_reexecucao`: 3 commits, `AfterEachRevision` lança exceção após a 2ª revisão; reexecutar o push ⇒ SVN termina com exatamente 3 revisões novas (não 5) e as mensagens não se repetem; `Log_contem_revisoes_criadas_e_erro`; `Dry_run_lista_o_que_seria_enviado`.
- [ ] **Step 2:** `dotnet test --filter RecoveryTests` ⇒ FAIL.
- [ ] **Step 3: Implementar** — retomada guiada pelos trailers `svn-id` e pelo `svn log` remoto antes de cada reenvio.
- [ ] **Step 4:** ⇒ PASS; `dotnet test` completo verde.
- [ ] **Step 5:** `git commit -m "feat: recuperacao de push interrompido e logs"`.

### Task 14: Empacotamento, instaladores e README

**Files:**
- Create: `eng/install.ps1`, `eng/install.sh`
- Create: `README.md`
- Modify: `src/GitRemoteSvn/GitRemoteSvn.csproj` (metadados de publish)

- [ ] **Step 1:** `dotnet publish src/GitRemoteSvn -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64` e o mesmo para `linux-x64`; conferir que `dist/win-x64/git-remote-svn.exe` existe e roda sem .NET instalado (`git-remote-svn.exe` sozinho imprime uso/erro amigável).
- [ ] **Step 2:** instalador copia o binário para `%LOCALAPPDATA%\Programs\git-remote-svn` (Windows) ou `~/.local/bin` (Linux), garante PATH e verifica `git --version` e `svn --version`; teste: rodar `eng/install.ps1` e `git clone svn::<fixture>` num terminal novo.
- [ ] **Step 3:** README com instalação, dia a dia (seção 8 da spec), limitações (seção 14 da spec: autor = conta SVN, sem externals/locks, pastas vazias, merge alheio importado como commit normal) e solução de problemas (`non-fast-forward`, conflito, `.git/svn/log`).
- [ ] **Step 4:** `git commit -m "chore: distribuicao single-file, instaladores e readme"`.

### Task 15: Validação no servidor real (vendas)

**Files:**
- Modify: `README.md` (resultados)

- [ ] **Step 1 (somente leitura):** `git clone svn::https://svn.example.com/svn/acme/projetos/web/vendas` em pasta temporária; comparar `git ls-tree -r main` + conteúdo com `svn export .../trunk` (hash por arquivo); conferir branches/tags importadas e `git log` com 21 revisões no caminho atual (`--stop-on-copy`).
- [ ] **Step 2 (somente leitura):** commit novo do time no trunk durante a validação ⇒ `git fetch` importa corretamente; se não houver commit novo, validar o incremental com o clone feito em dois momentos.
- [ ] **Step 3 (escrita — pedir confirmação explícita ao usuário antes):** criar `branches/git-remote-svn-teste-<data>` a partir do trunk, push de 2 commits + 1 merge, conferir pelo `svn log`/`svn propget svn:mergeinfo`, e remoção da branch de teste **também com confirmação**. Nada no `trunk`.
- [ ] **Step 4:** registrar resultados no README e `git commit -m "docs: validacao contra vendas"`.

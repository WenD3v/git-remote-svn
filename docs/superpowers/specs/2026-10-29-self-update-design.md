# Atualizacao do git-remote-svn pelo proprio helper

Data: 2026-10-29

## Por que

Quem instala pelo zip do release nao tem fonte nem clone: hoje a unica saida e baixar o zip de novo,
substituir o executavel na mao e reiniciar o terminal. O `eng/install.ps1` copia de `dist/win-x64`,
ou seja, so serve para quem compila. O objetivo e que a propria ferramenta cuide disso, como
`gh`, `pnpm` e `deno` fazem.

## O que entra

- `git-remote-svn --version`: hoje nao existe; e o que permite comparar versoes e reportar bug.
- `git-remote-svn update [--check] [--force]`: baixa o asset do release mais recente, confere o
  SHA-256 e troca o executavel em uso.
- Checagem automatica diaria com cache: no maximo uma consulta ao GitHub por dia, ao final de uma
  sessao normal (fetch/push), com aviso de uma linha no stderr quando ha versao nova.
- Auto-instalacao opcional: `git config --global remote-svn.autoUpdate true` ou
  `GIT_REMOTE_SVN_AUTO_UPDATE=1`. Sem isso, o padrao e so avisar.
- `SHA256SUMS` publicado no release (hoje so existem os dois arquivos compactados).

## O que nao entra

- Nao substitui gerenciador de pacote (winget/scoop/homebrew) — pode conviver depois.
- Nao atualiza quando o executavel e um `dotnet run` de desenvolvimento (ver Restricoes).
- Nao existe canal beta/canary, nem downgrade (`--force` reinstala a mesma versao).
- Sem assinatura criptografica: a confianca e HTTPS + GitHub + SHA-256 publicado no mesmo release
  (integridade contra download truncado/corrompido, nao contra conta comprometida).

## Restricoes descobertas

- **Windows nao sobrescreve executavel em uso.** A troca e: renomear `git-remote-svn.exe` para
  `git-remote-svn.exe.old`, gravar o novo no caminho original, apagar o `.old` na proxima
  execucao (best-effort). A sessao atual termina rodando o codigo antigo, ja carregado.
- **`dotnet run`/`dotnet publish` sem single-file**: `Environment.ProcessPath` e o `dotnet.exe`.
  Nesse caso `update` recusa com mensagem clara (instalacao de desenvolvimento).
- **Asset por plataforma**: `win-x64` (zip) e `linux-x64` (tar.gz) hoje. A escolha e por SO +
  arquitetura do processo; plataforma sem asset recebe "sem atualizacao para esta plataforma".
- **API do GitHub sem token**: 60 requisicoes/hora por IP. Com cache de 24h e rate-limit tratado
  como "desconhecido", e irrelevante — mas falha de rede nunca pode quebrar um fetch.
- **stderr do helper aparece no output do Git do editor**: o aviso precisa ser uma linha so e
  nunca antes/durante o protocolo.

## Abordagens consideradas

**A. So comando explicito.** Menor superficie, zero rede no dia a dia, mas quem nao roda o comando
fica velho — nao atende "automatico".

**B. Modelo do `gh` (escolhida).** Comando explicito + checagem diaria em cache + aviso curto +
auto-instalacao opt-in. Padrao de fato da industria, cobre Windows e Linux com o mesmo codigo,
mantem a latencia do fetch intacta (a consulta so acontece quando o cache vence, e falha e ignorada).

**C. Auto-update sempre ligado.** Mais magico e menos previsivel: troca binario no meio do uso,
antivirus/corporativo reclamando, dificil de auditar. Quase nenhuma ferramenta seria faz isso.

## Decisoes

1. **Fonte da verdade**: `GET https://api.github.com/repos/{repo}/releases/latest`, com
   `User-Agent: git-remote-svn/<versao>`. `repo` padrao `WenD3v/git-remote-svn`, sobrescrito por
   `GIT_REMOTE_SVN_UPDATE_REPO`; `GIT_REMOTE_SVN_UPDATE_API` troca a base da API (espelho interno e
   testes).
2. **Versao atual**: `AssemblyInformationalVersion` sem metadata (`+...`). Comparacao semantica
   numerica por componente (1.0.10 > 1.0.9), pre-release nunca e oferecida.
3. **Selecao do asset**: nome `git-remote-svn-<tag>-<os>-<arch>.(zip|tar.gz)`; aceita tanto o nome
   exato quanto o padrao por sufixo. `SHA256SUMS` e obrigatorio; sem ele ou com hash divergente,
   nada e instalado.
4. **Extracao**: zip por `System.IO.Compression`, tar.gz por `System.Formats.Tar` + `GZipStream`;
   procura `git-remote-svn.exe`/`git-remote-svn` na raiz do pacote.
5. **Instalacao**: no diretorio de `Environment.ProcessPath`. Linux: grava `.new` no mesmo
   diretorio, `chmod 0755`, `File.Move(..., overwrite: true)`. Windows: `.old` + escrita direta.
   Diretorio sem permissao de escrita => erro explicito com o comando manual.
6. **Cache da checagem**: `%LOCALAPPDATA%\git-remote-svn\update.json` no Windows;
   `$XDG_CACHE_HOME/git-remote-svn/update.json` ou `~/.cache/git-remote-svn/update.json` no resto.
   Conteudo: `checkedAtUtc`, `latestVersion`, `assetName` (nulo quando nao ha nada). TTL 24h;
   resultados negativos tambem sao gravados (offline nao vira tentativa a cada fetch).
7. **Quando checar**: ao final de uma sessao de helper (depois de `RunAsync`) e no `update --check`.
   Timeout de 2s, qualquer excecao e silenciosa, aviso so quando `latestVersion` for maior.
   `--quiet`/`GIT_REMOTE_SVN_QUIET=1` desliga o aviso (a auto-instalacao continua valendo).
8. **Auto-instalacao**: ligada por `remote-svn.autoUpdate=true` (global ou local) ou env. Roda no
   mesmo ponto da checagem, substitui o exe e imprime
   `git-remote-svn: atualizado para vX (novas sessoes usam a versao nova)`.
9. **`--version`**: imprime `git-remote-svn <versao> (<rid>)` e sai com 0; `version` tambem aceito.
10. **Workflow**: job `release` calcula `SHA256SUMS` dos dois pacotes com `sha256sum` e sobe junto
    no `gh release create`. Releases anteriores (v1.0.0/v1.0.1) nao tem o arquivo: `update` cai
    para a versao seguinte normalmente, porque o alvo e sempre o "latest".

## Estrutura de codigo

- `src/GitRemoteSvn/Update/UpdateService.cs`: consulta o release, compara versao, baixa, confere
  hash, extrai e troca o executavel (`CheckAsync`, `UpdateAsync`).
- `src/GitRemoteSvn/Update/UpdateCache.cs`: leitura/escrita e TTL do cache do usuario.
- `src/GitRemoteSvn/Update/UpdateOptions.cs`: repo/API/autoUpdate/quiet, lidos do ambiente e do git.
- `src/GitRemoteSvn/Update/VersionUtil.cs`: parse e comparacao de versao (puro, testavel).
- `src/GitRemoteSvn/Program.cs`: desvia `--version` e `update` antes do protocolo; ao final da
  sessao dispara a checagem (quando o cache venceu) sem afetar o codigo de saida.
- `tests/GitRemoteSvn.Tests/UpdateTests.cs`: versao, selecao de asset, `SHA256SUMS`, TTL do cache,
  parsing de argumentos e ciclo completo contra um `HttpListener` local servindo release falso
  (`GIT_REMOTE_SVN_UPDATE_API`), incluindo recusa por hash errado e troca do exe em copia temporaria.
- `README.md` e `README.pt-BR.md`: secao "Updating"/"Atualizando" com `update`, `--check` e o
  `remote-svn.autoUpdate`.
- `docs/design.md`: paragrafo curto sobre o canal de atualizacao.

## Riscos e mitigacoes

- Quebrar fetch por causa de rede => checagem fora do caminho critico, timeout curto, tudo engolido.
- Trocar o exe e falhar no meio => no Windows o original so e renomeado depois do download e da
  conferencia do hash; se a escrita falhar, o `.old` e restaurado.
- Antivirus bloqueando o exe novo => mensagem com o caminho do `.old` e instrucao de reinstalar pelo zip.
- Fork com outro repo => `GIT_REMOTE_SVN_UPDATE_REPO`.

> **Documento histórico** (o desenho e o plano usados na primeira versão, com nomes de servidor e de projetos trocados por exemplos). O comportamento atual está em [docs/design.md](../design.md).

# git-remote-svn — desenho

Data: 2026-10-28
Status: aguardando aprovacao do usuario
Projeto: `git-remote-svn` (repositorio Git local novo, fora do working copy SVN)

## 1. Intencao e por que

O time versiona em Subversion (servidor `https://svn.example.com/svn/acme`).
O usuario quer trabalhar 100% em Git — branches locais, worktrees por feature, merge na `main`,
`git log` rapido — sem obrigar ninguem do time a mudar de ferramenta e sem migrar o servidor.

Hoje nao existe na maquina nenhuma ponte utilizavel: o Git for Windows instalado
(2.55.0.windows.5) veio sem `git-svn` e sem Perl. As alternativas existentes nao servem:
`git svn` exige Perl e tem UX propria (`git svn dcommit`), SubGit e pago e fechado.

A solucao e uma ferramenta nossa, Windows e Linux, em que o dia a dia e Git literal:
`origin` e a URL do SVN, e `git clone` / `git fetch` / `git pull` / `git push` funcionam
sem alias nem script paralelo.

## 2. Objetivos

1. `git clone svn::<url-do-projeto-svn>` cria um repositorio Git completo: `trunk` -> `main`,
   `branches/*` -> branches remotas, `tags/*` -> tags, com historico completo.
2. `git fetch` / `git pull` trazem revisoes novas do SVN como commits Git, inclusive branches
   e tags criadas por quem usa so SVN.
3. `git push` envia commits locais ao SVN: **cada commit vira uma revisao**, com a mensagem
   original. **Merge commit vira uma unica revisao** com o resultado do merge, registrando
   `svn:mergeinfo` para quem usa SVN nao mergear a mesma branch de novo.
4. Criar branch no Git e dar push cria `branches/<nome>` no SVN por copia barata (`svn copy`),
   visivel e utilizavel por quem so usa SVN.
5. Nunca perder nem sobrescrever trabalho: nada de force push, nada de resolucao automatica
   de conflito, nada de descartar commit local.
6. Binario unico para Windows e Linux, sem runtime para instalar, usando o `svn` e o `git`
   que ja existem na maquina.

## 3. Nao-objetivos (v1)

- Substituir ou migrar o servidor SVN.
- Espelho bidirecional em tempo real / servico residente.
- Interface grafica.
- Suporte a `svn:externals` e ao fluxo de locks (`svn:needs-lock`, `svn lock`).
- Edicao de propriedades SVN arbitrarias pelo Git (exceto `svn:mergeinfo` no merge e
  leitura de `svn:ignore`).
- Compartilhar branches Git fora do SVN (a fonte da verdade e sempre o SVN).

## 4. Anti-objetivos (falha mesmo se "funcionar")

- Qualquer perda silenciosa: pull que sobrescreve commit local, push que sobrescreve revisao
  alheia, conflito resolvido sozinho.
- Corromper o historico do SVN ou criar revisoes vazias/duplicadas em sequencia.
- Exigir mudanca de comportamento de quem continua no TortoiseSVN.
- Clone inicial que inviabilize o uso (tempo ou disco) nos projetos reais.
- Ferramenta que so funciona na maquina do autor (caminhos fixos, credencial embutida).

## 5. Restricoes e fatos verificados

- Maquina de desenvolvimento: Windows, `C:\Users\dev`; .NET 9.0.318, Node 24, Python 3.14;
  **sem Go, sem Rust, sem Perl**.
- `git` 2.55.0.windows.5 em `C:\Program Files\Git`; `svn` 1.14.5 no PATH.
- Layout real no servidor (verificado em 2026-10-28):
  `projetos/<area>/<projeto>/{trunk,branches,tags}`, ex.: `projetos/web/app-web`,
  `projetos/android/pagamentos`, `projetos/servicos/assistente`.
  A raiz do repositório (`^/`) tambem contem pastas fora dessa convencao
  (`docs/`, `legado/`); a ferramenta so precisa entender projetos com
  `trunk/branches/tags` e, opcionalmente, uma pasta simples sem essa estrutura.
- Historicos pequenos: `web/app-web` 59 revisoes, `web/vendas` 21, `android/pagamentos` 17,
  `servicos/assistente` 7, `web/ui-kit` 3; projetos nasceram por volta de 07/out/2026.
  **Clone com historico completo e barato**; clone raso fica como opcao, nao como necessidade.
- Nomes de branch do time sao planos: `fix-1234-rolagem-lista`, `release-2026-10-26`.
  Tags por data: `2026-10-08-build3`.
- Nao ha `svn:externals` nem `svn:needs-lock` no trunk de `web/app-web`.
- Working copy SVN atual em `C:\svn-wc` (raiz do repo, r19681) **permanece intocada**;
  clones Git novos ficam fora dela (ex.: `C:\src\`).

## 6. Decisoes tomadas pelo usuario

1. Origem remota e o SVN: "e como se tudo fosse GIT, porem origin e o link do SVN".
2. Branches do Git **devem existir no SVN** em `branches/`, para o time dar continuidade
   mesmo usando so SVN.
3. Granularidade do push: **commit a commit**, com merge como revisao unica
   (recomendacao apresentada e aceita).
4. Clones novos **fora** de `C:\svn-wc`; o working copy SVN atual fica como rede de
   seguranca ate a ferramenta ser validada, e depois pode ser aposentado por projeto.
5. Codigo da ferramenta em `git-remote-svn`.

## 7. Escolhas do construtor (podem ser vetadas, nao precisam de resposta)

- **Linguagem/empacotamento:** C# / .NET 9, publicacao self-contained single-file para
  `win-x64` e `linux-x64`. Sem instalacao de runtime no destino.
- **Nome e integracao:** executavel `git-remote-svn` (`git-remote-svn.exe` no Windows) no PATH.
  O Git invoca automaticamente qualquer `git-remote-<esquema>`; por isso a URL e
  `svn::https://...` e os comandos sao os nativos.
- **Comunicacao com o SVN:** cliente de linha de comando `svn` (xml para `log`/`info`/`list`),
  nao biblioteca nativa. Autenticacao, certificados e cache de credencial ficam com o proprio
  SVN — a ferramenta nao guarda senha.
- **Comunicacao com o Git:** protocolo de remote helper (`capabilities`, `list`, `fetch`,
  `push`, `option`) na entrada/saida padrao; importacao de historico via `git fast-import`;
  leitura de commits para push via `git rev-list` / `git diff` / `git cat-file`.
- **Metadados da ponte:** dentro de `.git/svn/` (estado, cache) e refs `refs/svn/*` +
  trailer `svn-id: <url>@<rev> <uuid>` no fim das mensagens dos commits importados, para
  mapear commit <-> revisao de forma estavel e incremental.
- **Autores:** `svn:author` vira `Nome <usuario@svn.local>`; arquivo `autores.txt` opcional
  (`usuario = Nome <email>`) para mapear.
- **Autoria no push:** o SVN grava como autor a conta autenticada. Autor/data originais do Git
  sao preservados no objeto commit local, mas **nao** sao gravados como `svn:author`
  (o servidor nao permite). Limitacao documentada, mesmo comportamento do `git svn`.
- **Fim de linha:** conteudo importado byte a byte; clone configurado com `core.autocrlf=false` (e `core.longpaths=true`, para caminhos acima de 260 caracteres no Windows)
  para o Git nao reescrever arquivos e gerar diff falso no push.
- **`svn:ignore`:** convertido em `.git/info/exclude` (local, nao commitado), para nao criar
  `.gitignore` novo no SVN.
- **Diretorios vazios:** Git nao versiona; pastas vazias do SVN nao aparecem no clone
  (documentado).

## 8. Como fica o dia a dia

```bash
git clone svn::https://svn.example.com/svn/acme/projetos/android/pagamentos
cd pagamentos                 # main == trunk, origin/* == branches do SVN, tags importadas

git switch -c feature/totem-impressao
# ... commits ...
git push -u origin feature/totem-impressao
# -> svn copy trunk branches/feature/totem-impressao + uma revisao por commit

git switch main
git pull                     # fetch + rebase dos commits locais sobre as revisoes novas
git merge --no-ff feature/totem-impressao
git push origin main         # merge vira UMA revisao no trunk, com svn:mergeinfo
```

Quem usa TortoiseSVN ve apenas revisoes e pastas normais.

## 9. Regras de mapeamento

| SVN | Git |
|---|---|
| `trunk` | `main` (configuravel: `master`, `trunk`) |
| `branches/X` | `origin/X` |
| `branches/X/Y` (branch com `/` no nome) | `origin/X/Y` (mesma hierarquia; o helper distingue branch de subpasta olhando a raiz de cada branch criada por `svn copy`) |
| `tags/T` | tag `T` (anotada, apontando para o commit da copia) |
| revisao `rN` | commit com trailer `svn-id: ...@N` |
| branch/tag apagada no SVN | ref remota removida no fetch (aviso; nunca apaga branch local) |

## 10. Algoritmos (comportamento obrigatorio)

### 10.1 Clone / fetch
1. Descobrir layout (`svn info`/`svn list`): com `trunk/branches/tags` ou pasta simples.
2. Ler `svn log -v --xml` incremental por caminho, a partir da ultima revisao importada;
   na primeira importacao, `--stop-on-copy` (historico so do caminho atual, decisao do usuario).
3. Para cada revisao, obter o conteudo alterado (texto e binario) e alimentar
   `git fast-import`, criando commit com autor, data e mensagem da revisao + trailer `svn-id`.
4. Revisao de copia (`svn copy` de trunk para branches/X) vira branch Git apontando para o
   commit de origem, sem reimportar historico.
5. Revisoes que nao tocam o projeto sao ignoradas; revisoes que tocam varios projetos so
   importam os caminhos do projeto clonado.
6. Fetch nunca altera branches locais nem working tree; so refs remotas e tags.

### 10.2 Pull
- `git pull` usa a estrategia configurada no clone: **rebase** (padrao definido pelo helper
  em `git config branch.<nome>.rebase=true` na clonagem).
- Em conflito, o proprio Git para e pede resolucao. O helper nao resolve nada.

### 10.3 Push
1. Determinar o conjunto de commits a enviar: os commits locais que ainda nao tem `svn-id`
   na linha de primeira descendencia ate a ref remota.
2. **Pre-condicao:** buscar antes (`fetch` automatico). Se a ref remota avancou, o helper
   falha com orientacao de `git pull` (ou executa o rebase se `pull` tiver sido pedido),
   nunca sobrescreve.
3. Manter uma working copy SVN temporaria por branch em `.git/svn/wc/<branch>`, atualizada
   com `svn update` para a revisao da ref remota.
4. Para cada commit, na ordem:
   - commit normal: aplicar o diff (adicionar, modificar, apagar, renomear) na WC com
     `svn add`/`svn delete`/`svn propset` quando necessario; `svn commit -m <mensagem>`;
     registrar a revisao criada e gerar o commit espelho com `svn-id` (fast-import).
   - merge commit: aplicar o diff do merge contra o primeiro pai; **uma** revisao;
     gravar `svn:mergeinfo` com a origem (caminho da branch e intervalo de revisoes) antes
     do commit.
   - se o servidor responder *out of date*: abortar com seguranca (nenhum commit parcial
     silencioso), atualizar a WC, refazer fetch e orientar `git pull`; os commits ja
     aceitos ficam registrados como revisoes normais.
5. Atualizar a ref remota local para o ultimo commit espelho. O branch local do usuario
   passa a apontar para os commits espelho (rebase interno, como o `git svn dcommit`),
   preservando mensagens e autores.
6. Push de branch inexistente no SVN: `svn copy <origem> <url>/branches/<nome>` e entao o
   passo 4. Push de tag: `svn copy` para `tags/<nome>`; tag nao recebe commits depois
   (push de tag existente e recusado).
7. Nunca usar `--force`, nunca `svn commit` com `--no-unlock`, nunca apagar nada fora da
   WC temporaria.

### 10.4 Erros e recuperacao
- Toda operacao de rede com saida clara do que foi feito ate o erro (revisoes criadas).
- Estado em `.git/svn/` permite repetir o comando sem duplicar revisoes (idempotencia pelo
  trailer `svn-id` e pelo log remoto).
- `--dry-run` no push: mostra as revisoes que seriam criadas, sem tocar no servidor.
- Logs em `.git/svn/log/` para diagnostico.

## 11. Testes

Ambiente de teste **local e descartavel**: repositorio SVN criado em diretorio temporario
(`svnadmin create` + acesso `file://` e, se necessario, `svnserve`), com `trunk/branches/tags`
e pre-commit hook simulando conflito. Nenhum teste escreve no servidor real.

Cenarios minimos (automatizados, Windows; Linux em seguida):
1. Clone com historico completo, autores, datas, mensagens e binarios identicos.
2. Push linear de N commits -> N revisoes, mensagens preservadas, ordem correta.
3. Cliente B (svn puro) commita antes do push de A -> A e obrigado a atualizar; nenhuma
   revisao sobrescrita; commits de A entram depois.
4. Conflito de conteudo no rebase -> processo para com conflito; trabalho local intacto.
5. Branch criada no Git -> `svn copy` + commits; branch criada no SVN -> aparece no fetch.
6. Merge `--no-ff` -> uma revisao no trunk + `svn:mergeinfo` correto; re-merge pelo SVN nao
   repete alteracoes.
7. Tags nos dois sentidos; tag existente recusada.
8. Renomear/apagar arquivos e pastas; arquivos binarios; nomes com acento e espaco;
   caminhos longos no Windows (>260).
9. Interrupcao no meio do push -> reexecucao nao duplica revisoes.
10. Validacao real **somente leitura**: clone de `projetos/web/app-web` (59 revisoes, 7 branches,
    varias tags) e comparacao de conteudo contra `svn export` do trunk.

## 12. Entrega e migracao

- `git-remote-svn.exe` (Windows) e `git-remote-svn` (Linux) em `dist/`, com script de
  instalacao que copia para uma pasta no PATH e verifica `git`/`svn`.
- `README.md` com o dia a dia, limitacoes e solucao de problemas.
- Migracao por projeto: clonar em `C:\src\<projeto>`, validar, e so entao
  aposentar a working copy correspondente em `C:\svn-wc`. `C:\svn-wc` contem outras pastas
  usadas por documentacao e automacao; aposentar a working copy
  inteira e decisao separada, fora deste projeto.

## 13. Ajustes apos revisao do desenho (decisoes do construtor)

Revisao independente apontou 5 lacunas. Quatro ficam assim, como decisao do construtor:

1. **Merge cujo outro pai ainda nao existe no SVN** (branch so local): o push e recusado com
   a instrucao `git push -u origin <branch>` antes de repetir. O helper nao publica branches
   por conta propria. Merge com mais de dois pais (octopus) tambem e recusado na v1.
2. **Commit sem alteracao representavel no SVN** (commit vazio, ou merge cuja arvore e igual a
   do primeiro pai): nenhuma revisao vazia e criada. O commit e marcado como mapeado para a
   revisao corrente e o push avisa quantos commits ficaram sem revisao.
3. **Push de tag cujo commit ainda nao existe no SVN**: recusado, com orientacao de publicar
   a branch primeiro. Tag sempre nasce de `svn copy` de uma revisao existente.
4. **Tags sao imutaveis por convencao.** Se alguem commitar dentro de `tags/T` no SVN, o fetch
   avisa e **nao move** a tag local; o conteudo novo so entra por acao explicita do usuario.

5. **Historico: somente o caminho atual.** Decisao do usuario (2026-10-28): **nao** trazer o
   historico anterior a reorganizacao de out/2026. O clone usa `--stop-on-copy`: a historia de
   cada branch/trunk comeca na revisao em que aquele caminho foi criado, sem seguir
   `copyfrom-path` para fora do projeto (ex.: `projetos/web/app-web/trunk` veio de
   `/legado/app-web` na r16616; esse passado fica so no SVN). Branches e tags ja
   apagados no SVN tambem nao sao importados — apenas os caminhos existentes no HEAD.

## 14. Limitacoes aceitas (documentadas no README)

- Autor da revisao SVN = conta autenticada, nao o `git author` de commits de terceiros.
- Sem `svn:externals`, sem locks, sem propriedades arbitrarias.
- Pastas vazias nao existem no Git.
- Historico de merge entre branches feito por quem usa SVN puro e importado como alteracao
  de conteudo (a revisao de merge aparece como commit normal); `svn:mergeinfo` alheio nao
  vira merge commit no Git na v1.
- Renomeacao apenas de caixa (A.txt -> a.txt) no Windows exige cuidado e pode nao ser
  detectada.
- Clone de projeto sem `trunk/branches/tags` trata a pasta como tronco unico.

# git-remote-svn

*Read in English: [README.md](README.md)*

Use um repositório **Subversion** como `origin` de um repositório **Git**, com os comandos Git de sempre
(`clone`, `fetch`, `pull`, `push`). Quem continua no SVN (TortoiseSVN) vê apenas revisões e pastas normais.

É um *remote helper* do Git: o Git o chama sozinho quando a URL começa com `svn::`.

## Instalação

Pré-requisitos: `git` e o cliente de linha de comando `svn` no PATH (no Windows, TortoiseSVN com
"command line client tools" ou SlikSVN; no Linux, `sudo apt install git subversion`). Não precisa de .NET.
O `svnrdump`, que vem junto com o `svn` nesses pacotes, deixa o clone e o fetch bem mais rápidos; sem ele o
helper funciona, só que mais devagar (um `svn cat` por arquivo alterado).

Pelo release: baixe o pacote da sua plataforma em [Releases](https://github.com/WenD3v/git-remote-svn/releases),
extraia e coloque `git-remote-svn` (`git-remote-svn.exe` no Windows) numa pasta do `PATH`.

Pelo código-fonte (.NET 9 SDK):

```powershell
# Windows (gera e instala em %LOCALAPPDATA%\Programs\git-remote-svn, já no PATH do usuário)
dotnet publish src/GitRemoteSvn -c Release -r win-x64 -o dist/win-x64
powershell -ExecutionPolicy Bypass -File eng\install.ps1
```

```bash
# Linux (instala em ~/.local/bin)
dotnet publish src/GitRemoteSvn -c Release -r linux-x64 -o dist/linux-x64
eng/install.sh
```

Abra um terminal novo (e reinicie o editor por completo) depois de instalar. As credenciais ficam no cache do próprio `svn`: se o servidor pedir
senha, rode uma vez `svn info <url>` e salve a senha.

## Dia a dia

```bash
git clone svn::https://svn.example.com/svn/acme/projetos/android/pagamentos
cd pagamentos                 # main == trunk, origin/* == branches do SVN, tags importadas

git switch -c feature/totem-impressao
# ... commits ...
git push -u origin feature/totem-impressao
# -> svn copy trunk branches/feature/totem-impressao + uma revisão por commit

git switch main
git pull                     # fetch + rebase dos commits locais sobre as revisões novas
git merge --no-ff feature/totem-impressao
git push origin main         # o merge vira UMA revisão no trunk, com svn:mergeinfo
git tag v1.2 && git push origin v1.2   # svn copy para tags/v1.2
```

| SVN | Git |
|---|---|
| `trunk` | `main` |
| `branches/X` (e `branches/X/Y`) | `origin/X` (`origin/X/Y`) |
| `tags/T` | tag `T` |
| revisão `rN` | commit com a linha `svn-id: <url>@N <uuid>` no fim da mensagem |
| branch/tag apagada no SVN | some de `origin/*` no próximo fetch (com aviso); a branch local nunca é apagada |

Como funciona o push:

- Cada commit novo vira **uma revisão** com a mensagem original. Depois disso a sua branch local passa a apontar
  para cópias desses commits com a linha `svn-id` (mesmo conteúdo, autor e data) — é assim que o Git sabe o que
  já está no SVN. `git status` continua limpo.
- **Ninguém sobrescreve ninguém.** Se alguém commitou no SVN depois do seu último fetch, o push é recusado
  e você roda `git pull` (rebase). Nunca há `--force`.
- Merge só é aceito se a branch mergeada já estiver no SVN (`git push -u origin <branch>` antes); merge de
  várias branches de uma vez (octopus) é recusado.
- Commit vazio (ou merge que não muda nada) não cria revisão; o push avisa.
- Tags são imutáveis: push de tag que já existe no SVN é recusado, mesmo com `--force`. A tag precisa apontar
  para um commit já publicado.
- `git push --dry-run` mostra o que viraria revisão, sem tocar no servidor.

O clone traz só o histórico do caminho atual (`--stop-on-copy`): para projetos reorganizados em pastas novas,
o passado anterior à cópia continua só no SVN.

## Desempenho

As revisões chegam por um único `svnrdump dump` por branch (uma conexão, só os deltas), e o conteúdo é
montado localmente. Medido num projeto real (31 revisões no trunk, 7 branches, 10 tags, ~2.500 arquivos via
HTTPS):

| | `svn cat` por arquivo (antigo) | `svnrdump` (atual) |
|---|---|---|
| `git clone` | 786 s | **182 s** |
| `git fetch` sem novidades | — | 12 s |

Branch ou tag criada por cópia **com arquivos alterados na mesma revisão** ainda
busca esses arquivos um a um nessa revisão. Para medir onde o tempo vai, defina
`GIT_REMOTE_SVN_TRACE=<arquivo>`: cada processo externo é registrado com a duração.

## Limitações

- O autor da revisão SVN é a **conta autenticada** no `svn`, não o autor do commit Git (o autor original fica
  preservado no Git). Mesmo comportamento do `git svn`.
- Sem `svn:externals`, sem locks (`svn:needs-lock`) e sem propriedades SVN arbitrárias (só `svn:mergeinfo` nos merges
  e `svn:ignore` da raiz e das subpastas, convertido em `.git/info/exclude`; `svn:global-ignores` não é lido). Os `svn:ignore` são atualizados quando
  a branch recebe revisões novas, não a cada fetch.
- Pastas vazias não existem no Git.
- Merge feito por quem usa SVN puro chega como commit normal (alteração de conteúdo), não como merge do Git.
- Renomear só a caixa do nome (`A.txt` → `a.txt`) no Windows pode não ser detectado.
- Arquivos são mantidos byte a byte: o clone grava `core.autocrlf=false` e `* -text` em `.git/info/attributes`,
  para o Git não converter fins de linha.
- Projeto sem `trunk/branches/tags` é tratado como tronco único (só `main`).
- No Windows, arquivos com `svn:eol-style=native` chegam com CRLF (o `svn` converte ao exportar); num clone Linux
  vêm com LF.

## Validação num servidor real (28/10/2026)

| Projeto | Resultado |
|---|---|
| `web/vendas` (leitura) | clone em 69 s; 21 commits = 21 revisões do caminho atual; 223/223 arquivos idênticos (SHA-256) ao `svn export` do trunk; `git fetch` sem novidades em 2 s |
| `web/app-web` (leitura) | 31 commits = 31 revisões; 7/7 branches e 10/10 tags; trunk (2564 arquivos), 2 branches e 1 tag idênticos arquivo a arquivo ao `svn export` |
| `web/vendas` (escrita) | `git push -u` criou `branches/git-remote-svn-teste-20261028` por `svn copy` (r19761); 2 commits → r19762 e r19763 com as mensagens originais; branch lateral publicada (r19764–r19765) e merge `--no-ff` → **uma** revisão r19766 com `svn:mergeinfo` `/…/git-remote-svn-teste-20261028-lado:19764-19765`; trunk intocado (r19571); branches removidas por `svn rm` (r19767) e o `git fetch` seguinte tirou as duas do clone |

A primeira tentativa no `app-web` caiu por timeout de rede (`E175012`) no meio do clone: daí o retry automático nos
comandos de leitura do `svn` e a cópia de pastas por um único `svn export`.

## Solução de problemas

| Mensagem | O que fazer |
|---|---|
| `git: 'remote-svn' is not a git command` | O `git-remote-svn` não está no `PATH` do processo que roda o Git. Abra um terminal novo ou reinicie o editor por completo depois de instalar. |
| `! [rejected] main -> main (fetch first)` ou `non-fast-forward` | O SVN tem revisões novas. `git pull` e repita o push. |
| `o SVN recebeu revisoes novas desde o ultimo fetch; rode git pull` | Idem: alguém commitou entre o fetch e o push. |
| Conflito durante o `git pull` | É o rebase normal do Git: resolva, `git add`, `git rebase --continue`. Nada foi enviado ao SVN. |
| `publique a branch antes: git push -u origin <b>` | O merge traz commits que não estão no SVN. Publique a branch e repita. |
| `o historico local tem um merge feito pelo git pull sem rebase` | `git pull --rebase` e repita o push (o clone já grava `pull.rebase=true`; isso aparece quando alguém usou `--no-rebase`). |
| `outro git-remote-svn esta rodando neste repositorio` | Um fetch automático do editor ou outro terminal está usando o repositório. Repita em instantes. |
| Push interrompido (queda de rede, terminal fechado) | Rode o mesmo `git push` de novo: as revisões que já entraram são reconhecidas e não se repetem. |
| Qualquer outro erro | Veja o log da sessão em `.git/svn/log/` (o caminho aparece na mensagem de erro). |

Estado interno fica em `.git/svn/` (revisões importadas, cópias de trabalho temporárias em `.git/svn/wc`, logs).

## Desenvolvimento

```bash
dotnet test        # cria repositórios SVN descartáveis (svnadmin) em %TEMP%; nunca toca servidor real
```

Arquitetura: [docs/design.md](docs/design.md) (em inglês). Desenho e plano originais: [docs/history/](docs/history/).

## Contribuindo

Issues e pull requests são bem-vindos (em português ou inglês). Leia o [CONTRIBUTING.md](CONTRIBUTING.md)
antes. O projeto segue o [Contributor Covenant](CODE_OF_CONDUCT.md); falhas de segurança: [SECURITY.md](SECURITY.md).

## Licença

[MIT](LICENSE) © 2026 Wendel Marques

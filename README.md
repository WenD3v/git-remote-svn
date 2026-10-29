# git-remote-svn

[![CI](https://github.com/WenD3v/git-remote-svn/actions/workflows/ci.yml/badge.svg)](https://github.com/WenD3v/git-remote-svn/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

*Leia em português: [README.pt-BR.md](README.pt-BR.md)*

Use a **Subversion** repository as the `origin` of a **Git** repository, with the Git commands you already
know: `clone`, `fetch`, `pull`, `push`. Teammates who stay on SVN (TortoiseSVN, `svn` CLI) only ever see
ordinary revisions and folders.

It is a Git [remote helper](https://git-scm.com/docs/gitremote-helpers): Git runs it automatically whenever a
remote URL starts with `svn::`. No `git svn dcommit`, no Perl, no aliases.

```bash
git clone svn::https://svn.example.com/svn/acme/projects/web/shop
cd shop
git switch -c feature/checkout
# ... commits ...
git push -u origin feature/checkout   # creates branches/feature/checkout in SVN, one revision per commit
```

## Why

- **Plain Git workflow** on top of an SVN server you cannot (or do not want to) migrate: local branches,
  worktrees, interactive rebase, fast `git log`.
- **Nobody overwrites anybody.** A push that is behind SVN is refused, pull always rebases, there is no
  `--force` path, and conflicts are always left to you.
- **Single self-contained binary** for Windows and Linux. No .NET runtime, no Perl; it drives the `git` and
  `svn` you already have.

## Installation

Requirements: `git` and the command-line `svn` client on `PATH`.

- Windows: TortoiseSVN with *command line client tools*, or SlikSVN.
- Linux: `sudo apt install git subversion` (or your distribution's equivalent).

`svnrdump`, which ships with `svn` in those packages, makes clone and fetch much faster. Without it the
helper still works, only slower (one `svn cat` per changed file).

### From a release

Download the archive for your platform from [Releases](https://github.com/WenD3v/git-remote-svn/releases),
extract it and put `git-remote-svn` (`git-remote-svn.exe` on Windows) in a folder on your `PATH`.

### Updating

```bash
git-remote-svn --version       # git-remote-svn 1.0.2 (win-x64)
git-remote-svn update --check  # is there a newer release?
git-remote-svn update          # download, verify SHA256SUMS and replace this executable
```

The helper also checks once a day in the background (cached, 2 s timeout) and prints a single line when a newer
version exists, for example `git-remote-svn: nova versao v1.0.3 disponivel (git-remote-svn update)`. To install it
automatically at the end of a session, run `git config --global remote-svn.autoUpdate true` (or set
`GIT_REMOTE_SVN_AUTO_UPDATE=1`); `--quiet` / `GIT_REMOTE_SVN_QUIET=1` silence the notice, and
`GIT_REMOTE_SVN_NO_UPDATE=1` turns the end-of-session check off entirely. On Windows the running
binary is renamed and replaced, so the current session keeps the old version and the next one starts on the new.
Releases before v1.0.2 have no `SHA256SUMS` and are refused by `update` — install those by hand.

### From source (.NET 9 SDK)

```powershell
# Windows: installs into %LOCALAPPDATA%\Programs\git-remote-svn and adds it to the user PATH
dotnet publish src/GitRemoteSvn -c Release -r win-x64 -o dist/win-x64
powershell -ExecutionPolicy Bypass -File eng\install.ps1
```

```bash
# Linux: installs into ~/.local/bin
dotnet publish src/GitRemoteSvn -c Release -r linux-x64 -o dist/linux-x64
eng/install.sh
```

Open a **new** terminal (and restart your editor completely) after installing, so the new `PATH` is picked
up. Credentials stay in `svn`'s own cache: if the server asks for a password, run `svn info <url>` once and
save it.

## Everyday use

```bash
git clone svn::https://svn.example.com/svn/acme/projects/android/payments
cd payments                  # main == trunk, origin/* == SVN branches, SVN tags imported

git switch -c feature/receipt-printer
# ... commits ...
git push -u origin feature/receipt-printer
# -> svn copy trunk branches/feature/receipt-printer + one revision per commit

git switch main
git pull                     # fetch + rebase your local commits on top of the new revisions
git merge --no-ff feature/receipt-printer
git push origin main         # the merge becomes ONE revision on trunk, with svn:mergeinfo
git tag v1.2 && git push origin v1.2   # svn copy to tags/v1.2
```

| SVN | Git |
|---|---|
| `trunk` | `main` |
| `branches/X` (and `branches/X/Y`) | `origin/X` (`origin/X/Y`) |
| `tags/T` | tag `T` |
| revision `rN` | commit ending with the line `svn-id: <url>@N <uuid>` |
| branch/tag deleted in SVN | disappears from `origin/*` on the next fetch (with a warning); local branches are never deleted |

### How push works

- Each new commit becomes **one revision** with the original message. Your local branch is then moved to
  copies of those commits carrying the `svn-id` line (same tree, author and date); that is how Git knows what
  is already in SVN. `git status` stays clean.
- **Nobody overwrites anybody.** If someone committed to SVN after your last fetch, the push is refused and
  you run `git pull` (rebase). There is never a `--force`.
- A merge is accepted only if the merged branch is already in SVN (`git push -u origin <branch>` first).
  Octopus merges are refused.
- An empty commit (or a merge that changes nothing) creates no revision; the push tells you.
- Tags are immutable: pushing a tag that already exists in SVN is refused, even with `--force`. The tag must
  point to an already published commit.
- `git push --dry-run` shows what would become revisions without touching the server.
- An interrupted push (network drop, closed terminal) can simply be repeated: revisions that already went in
  are recognized and not duplicated.

Clone brings only the history of the current path (`--stop-on-copy`): for projects that were moved to new
folders, the history before the copy stays in SVN only.

## Performance

Revisions arrive through a single `svnrdump dump` per branch (one connection, deltas only) and file contents
are rebuilt locally. Measured on a real project (31 trunk revisions, 7 branches, 10 tags, ~2,500 files over
HTTPS):

| | Per-file `svn cat` (old) | `svnrdump` (current) |
|---|---|---|
| `git clone` | 786 s | **182 s** |
| `git fetch` with nothing new | — | 12 s |

A branch or tag created by a copy **with modified files in the same revision** still fetches those files one
by one in that revision. To see where time goes, set `GIT_REMOTE_SVN_TRACE=<file>`: every external process is
logged with its duration.

## Limitations

- The author of the SVN revision is the **authenticated `svn` account**, not the Git commit author (the
  original author is kept in Git). Same behavior as `git svn`.
- No `svn:externals`, no locking (`svn:needs-lock`), no arbitrary SVN properties. Only `svn:mergeinfo` (on
  merges) and `svn:ignore` (root and subfolders, converted into `.git/info/exclude`) are handled;
  `svn:global-ignores` is not read. `svn:ignore` is refreshed when a branch receives new revisions.
- Empty folders do not exist in Git.
- A merge made by an SVN-only user arrives as a normal commit (content change), not as a Git merge.
- Case-only renames (`A.txt` → `a.txt`) on Windows may go undetected.
- Files are kept byte for byte: the clone sets `core.autocrlf=false` and `* -text` in `.git/info/attributes`.
- On Windows, files with `svn:eol-style=native` arrive with CRLF (as `svn export` does); on Linux, with LF.
- A project without `trunk/branches/tags` is treated as a single trunk (`main` only).
- Messages printed by the helper are currently in Portuguese. Translations are welcome.

## Troubleshooting

| Message | What to do |
|---|---|
| `git: 'remote-svn' is not a git command` | `git-remote-svn` is not on the `PATH` of the process running Git. Open a new terminal, or fully restart your editor/IDE after installing. |
| `! [rejected] main -> main (fetch first)` or `non-fast-forward` | SVN has new revisions. `git pull`, then push again. |
| `o SVN recebeu revisoes novas desde o ultimo fetch; rode git pull` | Someone committed between your fetch and your push. `git pull`, then push again. |
| Conflict during `git pull` | It is Git's normal rebase: resolve, `git add`, `git rebase --continue`. Nothing was sent to SVN. |
| `publique a branch antes: git push -u origin <b>` | The merge brings commits that are not in SVN. Publish the branch and retry. |
| `o historico local tem um merge feito pelo git pull sem rebase` | `git pull --rebase` and push again (the clone sets `pull.rebase=true`; this shows up after `--no-rebase`). |
| `outro git-remote-svn esta rodando neste repositorio` | An editor auto-fetch or another terminal is using the repository. Try again in a moment. |
| Anything else | See the session log in `.git/svn/log/` (its path is printed with the error). |

Internal state lives in `.git/svn/` (imported revisions, temporary SVN working copies in `.git/svn/wc`, logs).

## Documentation

- [docs/design.md](docs/design.md): architecture, data flow and safety rules (start here to contribute).
- [docs/history/](docs/history/): the original design and implementation plan (Portuguese).

## Contributing

Issues and pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. This project
follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Security issues: see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Wendel Marques

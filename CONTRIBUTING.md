# Contributing to git-remote-svn

Thanks for your interest! Issues and pull requests are welcome, in English or Portuguese.

## Before you start

- **Bug?** Open an issue with the bug template. Include `git --version`, `svn --version`, your OS, the exact
  command, and the session log from `.git/svn/log/` (remove server names and paths you do not want public).
- **Feature or behavior change?** Open an issue first so we can agree on the approach before you write code.
  Small fixes (typos, docs, obvious bugs) can go straight to a pull request.
- **Security problem?** Do not open an issue; see [SECURITY.md](SECURITY.md).

Read [docs/design.md](docs/design.md) before changing import or push code.

## The one rule

**Nobody overwrites anybody.** The helper must never lose a local commit, overwrite someone else's SVN
revision, skip a revision on fetch, or resolve a conflict on its own. A pull request that touches push,
import resume, refs or `.git/svn` state must include a test showing the guarantee still holds.

## Development setup

You need:

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- `git`
- Subversion command-line tools: `svn`, `svnadmin` and `svnrdump` on `PATH`
  - Windows: TortoiseSVN (with *command line client tools*) or SlikSVN
  - Linux: `sudo apt install subversion`

```bash
dotnet build
dotnet test
```

Tests create throwaway SVN repositories (`svnadmin create`, `file://`) in the temp folder and run real `git`
and `svn` end to end. They never contact a real server, and they must stay that way.

To try your build against a repository:

```bash
dotnet publish src/GitRemoteSvn -c Release -r linux-x64 -o dist/linux-x64   # or win-x64
PATH="$PWD/dist/linux-x64:$PATH" git clone svn::file:///path/to/repo/project
```

Set `GIT_REMOTE_SVN_TRACE=/tmp/trace.txt` to log every external command with its duration.

## Pull requests

1. Fork, create a branch from `main`.
2. Write the test first and see it fail; then make it pass. Every bug fix comes with a test that fails without
   the fix.
3. Keep the change focused: one topic per pull request.
4. `dotnet test` must pass. CI runs on Windows and Linux.
5. Update `README.md` / `README.pt-BR.md` when user-visible behavior changes.
6. Use a short, imperative commit title (`fix: ...`, `feat: ...`, `docs: ...`, `test: ...`, `perf: ...`).

## Code style

- Match the surrounding code: C# with nullable enabled, `async` all the way down, small focused classes.
- External tools are always invoked through `Util/Proc.cs` with an argument list (never a shell string).
- User-facing messages are currently in Portuguese. New messages may be in either language; a proper
  translation layer is a welcome contribution.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).

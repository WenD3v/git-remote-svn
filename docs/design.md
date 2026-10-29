# Design

This document describes how `git-remote-svn` works today. It is the starting point for contributors.
The original design and implementation plan (Portuguese) are kept in [history/](history/).

## Goals and non-negotiables

1. Day-to-day work is literal Git: `origin` is the SVN URL (`svn::https://...`) and `clone`, `fetch`, `pull`,
   `push` work without aliases or side scripts.
2. SVN-only teammates notice nothing unusual: Git branches are real `branches/<name>` folders created by
   `svn copy`, every Git commit is a normal revision, a merge is one revision with `svn:mergeinfo`.
3. **Never lose or overwrite work.** No force push, no automatic conflict resolution, no discarded local
   commits, no silent skipping of someone else's revision. Any change that weakens this needs a test that
   proves the guarantee still holds.
4. One self-contained binary per platform (win-x64, linux-x64) that drives the existing `git` and `svn`.

Out of scope for now: `svn:externals`, locking, arbitrary SVN properties, a resident mirror service, a GUI.

## Big picture

```
git clone/fetch/push svn::URL
        │  (stdin/stdout, gitremote-helpers protocol)
        ▼
 Helper/RemoteHelper ──► Sync/RevisionImporter ──► svnrdump dump / svn log ──► Sync/DumpApplier
        │                         │                                                  │
        │                         └──────────────► Git/FastImportWriter (git fast-import)
        │
        └──────────────► Sync/Pusher ──► Sync/SvnWorkingCopy (svn update/add/delete/commit)
```

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point invoked by Git (`git-remote-svn <remote> <url>`). Strips `svn::`, takes the repository lock (`.git/svn/lock`), runs `RemoteHelper`. |
| `Helper/RemoteHelper.cs` | Speaks the remote-helper protocol: `capabilities`, `option`, `list`, `fetch`, `push`. Routes pushes to branches/tags and fixes tracking refs at the end of the session. |
| `Svn/SvnClient.cs` | Thin wrapper over the `svn`/`svnrdump` CLIs (XML output). Retries idempotent read commands on network errors; never retries `commit`/`copy`. |
| `Svn/SvnDumpReader.cs`, `Svn/SvnDiff.cs` | Streaming reader for the SVN dump format and an svndiff v0/v1 delta applier. |
| `Sync/ProjectLayout.cs` | Detects `trunk/branches/tags` vs. a single-trunk folder. |
| `Sync/RevisionImporter.cs` | Imports trunk, branches and tags into private refs. Chooses between the dump path and the per-revision fallback. |
| `Sync/DumpApplier.cs` | Rebuilds each revision's tree from dump records (deltas, copies, EOL translation, `svn:ignore`). |
| `Sync/Pusher.cs` | Turns local commits into SVN revisions and rewrites the local branch onto mirror commits. |
| `Sync/SvnWorkingCopy.cs` | Per-branch shallow scratch working copy in `.git/svn/wc/`; fetches only the paths a commit touches. |
| `Sync/StateStore.cs` | `.git/svn/state.json`: layout, last imported revision per ref, revisions created by in-flight pushes, repository HEAD at the last import. |
| `Sync/SvnId.cs` | The `svn-id: <url>@<rev> <uuid>` trailer: the single source of truth for "this commit is that revision". |
| `Git/GitClient.cs`, `Git/FastImportWriter.cs` | Git CLI wrapper and `git fast-import` stream writer. |
| `Util/Proc.cs` | Process runner; honors `GIT_REMOTE_SVN_TRACE`. |

## Refs and state

- Imported history lives in **private refs**: `refs/svn/<remote>/heads/<branch>` and
  `refs/svn/<remote>/tags/<tag>`. `list` advertises them as `refs/heads/*` / `refs/tags/*`, and Git itself
  creates `refs/remotes/<remote>/*` from that. The helper never writes `refs/remotes/*` directly, except to
  correct the tracking ref after a push.
- Every imported or pushed commit ends with an `svn-id` trailer. On fetch, the importer resumes from the
  `svn-id` of the private ref's tip; `state.json` is only a cache, and is written after `fast-import` has
  finished. That is what keeps an interrupted fetch from silently skipping revisions.
- All helper state uses `git rev-parse --git-common-dir`, so linked worktrees share one state.
- The clone writes `core.autocrlf=false`, `core.longpaths=true`, `pull.rebase=true`,
  `branch.<name>.rebase=true`, `remote.<remote>.prune=true`, and `* -text` in `.git/info/attributes`.

## Import (clone and fetch)

1. `svn log -v --xml` lists the revisions of each branch since the last imported one
   (`--stop-on-copy` on the first import: history starts where the current path was created).
2. With `svnrdump` available, one `svnrdump dump` per branch (incremental when the ref exists) streams every
   revision. `DumpApplier` keeps the tree as path → blob, applies text deltas against the previous content,
   resolves copies from earlier revisions (inside the dump, or from already imported commits), validates
   MD5s, and translates `svn:eol-style` the way `svn export` does (native = CRLF on Windows, LF on Linux).
   Only changed files are written to `fast-import`.
3. Without `svnrdump`, or for copies whose source is outside the branch, the importer falls back to the
   per-revision path: `svn cat` per changed file, `svn export` for copied folders.
4. A new branch or tag created by `svn copy` starts from the commit of the copy source (found through the
   `svn-id` trailers), so shared history is never duplicated.
5. Deleted branches/tags drop their private ref (with a warning). Tags are immutable: if someone commits
   inside `tags/T`, fetch warns and does not move the tag.
6. `svn:ignore` values become patterns in `.git/info/exclude` (local, never committed).
7. Fetch never touches local branches or the working tree.
8. Every session starts with one `svn info` on the project root: if the repository HEAD and UUID match the
   ones recorded in `state.json` at the last complete import, nothing changed and the per-branch checks are
   skipped. A fetch with no news costs about one `svn info` instead of a dozen calls.

## Push

For a branch that already exists in SVN (`Pusher.PushBranchAsync`):

1. Validate first, write later: merges with more than two parents are refused; a merge whose second parent
   has no `svn-id` is refused with `git push -u origin <branch>`; the first commit's parent must be the
   remote tip (otherwise: `git pull --rebase`).
2. Precondition: the branch's last-changed revision must not be newer than what was imported, otherwise the
   push is refused (`git pull`). The only exception is revisions recorded in `state.json` by an interrupted
   push of this repository, which are adopted in order.
3. For each commit along `--first-parent`: update the scratch working copy, apply the
   `diff-tree --no-renames` changes, `svn commit` with the original message, verify that nobody committed in
   between, then create a mirror commit (`commit-tree`: same tree, author, committer and parents, plus the
   `svn-id` trailer). A merge also sets `svn:mergeinfo`. An empty commit creates no revision.

   The scratch copy is a shallow checkout (`--depth empty`): only the directories and files the commit
   touches are fetched, by a recursive `svn list` plus `svn update` of those paths. A file the copy already
   holds from an earlier push has its base revision refreshed first, otherwise committing over somebody
   else's change would fail as "out of date" even with the Git side already pulled. A full checkout of a
   large tree cost 10-18 s per push; the shallow copy costs about the same as one `svn list`.
4. Only at the end: advance the private ref and the state, and move the local branch onto the mirror commits
   with a compare-and-swap `update-ref`.

A new branch is `svn copy` from the revision of its last published commit, then a linear push. A tag is
`svn copy` of a published commit into `tags/`; existing tags are refused.

## Testing

`dotnet test` creates throwaway SVN repositories with `svnadmin create` and `file://` URLs in the temp folder,
publishes the helper once, and drives real `git` and `svn` end to end. Tests never contact a real server. Some
tests replace `svn` with a fake script to simulate network errors or forbid calls; those run on Windows only.

When you fix a bug, add a test that fails without the fix. Changes to push, import resume or state handling
should come with a test for the "nobody overwrites anybody" guarantee they touch.

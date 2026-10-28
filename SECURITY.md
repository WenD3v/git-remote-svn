# Security Policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/WenD3v/git-remote-svn/security/advisories/new).

Include the version (`git-remote-svn` release or commit), your OS, and steps to reproduce. You should get a
first answer within a week.

Things that count as security issues here include, for example: leaking SVN credentials or URLs with embedded
passwords into logs or commit messages, command injection through file names, branch names or commit
messages, and writing outside the repository or its `.git/svn` folder.

Bugs that can make the helper lose or overwrite someone's work in SVN or Git are not security issues, but
they are treated as the highest priority: please report them as a bug with the `data-safety` details filled in.

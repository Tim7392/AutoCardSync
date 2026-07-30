# Contributing to AutoCardSync Standalone

Thank you for helping improve AutoCardSync Standalone.

## Before opening a change

- Read the repository [README](README.md), [SECURITY.md](SECURITY.md), and [LICENSE](LICENSE).
- Keep changes focused. Do not combine feature work with unrelated formatting, generated files, or local-environment cleanup.
- Use a branch and describe the user-visible behavior, risk, and verification you performed.
- Never add card contents, media files, runtime state, receipts, logs, credentials, private keys, NAS addresses, local paths, installation output, or customer information to the repository.

## Reproductions and tests

- Reproduce transfer and recovery issues only with synthetic or fully redacted data.
- Do not format, erase, repair, or modify a real source card as part of a contribution.
- State which verification was actually run. Do not present static review, CI artifacts, or a successful build as physical-card or production acceptance.
- Generated binaries, installers, prerequisites, coverage, and verification artifacts belong in CI or an approved private channel, not in commits.

## Pull requests

A pull request should include:

1. The problem and the intended outcome.
2. The affected component and safety assumptions.
3. The verification performed and any unverified hardware, NAS, signing, or release gates.
4. Any migration, recovery, or compatibility impact.

Security-sensitive findings must follow [SECURITY.md](SECURITY.md), not the pull-request process.

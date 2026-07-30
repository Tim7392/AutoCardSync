# AutoCardSync Standalone

AutoCardSync Standalone is a Windows application for importing approved media from removable cards to local storage and, when configured, an independent NAS destination. It is designed for careful transfer and recovery workflows rather than destructive card management.

> **Project status:** pre-release. The source and CI artifacts are not a statement that a build has passed real-card, reader, NAS, power-loss, installation, signing, or production acceptance.

## What Standalone does

- Detects inserted removable media and guides configuration for each recognized card.
- Copies only approved folders and file types to the configured destination or destinations.
- Uses temporary objects, resumable checkpoints, verification, final publication, and a completion receipt before reporting that a transfer is safe to remove.
- Treats unexpected card removal, source or target identity changes, unavailable destinations, and inconsistent recovery state as failures that require review.
- Keeps local and NAS destinations as separate completion targets; an NAS copy is not assumed to be an independent backup merely because it is mapped on the same machine.

## Scope and limitations

- Current development target: Windows x64 with the .NET desktop runtime and Microsoft Edge WebView2 Runtime.
- Physical-media and storage safety depend on the real devices, card reader, destination disks, network, and NAS configuration in use. Use non-production copies for any evaluation.
- The project does not promise production readiness or a public release cadence at this time.
- Very large file-count and checkpoint-count workloads remain release-gated. The current safety design keeps source identity evidence open for the batch and persists recovery checkpoints conservatively; this favors recoverability over minimum handle count and minimum journal-write amplification.
- Do not put card contents, customer data, NAS addresses, runtime state, receipts, logs, credentials, or installation artifacts in issues or pull requests.

## Build boundaries

The public solution contains only the Standalone V1 application, its required shared domain project, installer/bootstrapper projects, and Standalone tests.

```powershell
dotnet build AutoCardSync.Standalone.sln -c Release
```

The repository contains a Standalone-specific packaging route:

```powershell
pwsh -NoProfile -File tools/Build-StandaloneSetup.ps1 `
  -Configuration Release `
  -ProductVersion <a.b.c.d> `
  -SetupVersion <a.b.c>
```

That route validates the pinned offline runtime prerequisites, builds the Standalone application and MSI, creates the Setup executable, and runs the repository's static bundle validator. The prerequisite binaries and generated artifacts are deliberately not committed.

GitHub Actions runs the Standalone Core build and the same static setup-validation route for `main`. Each CI artifact includes a manifest binding the built commit, requested version, and SHA-256 values. The manual candidate workflow creates **unsigned candidate evidence only**; it does not publish a GitHub Release, sign files, or claim external acceptance.

## Contributing

Please read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing a change. Use synthetic or fully redacted data in all examples, diagnostics, and reproduction steps.

## Security

Please read [SECURITY.md](SECURITY.md). Report vulnerabilities through GitHub private vulnerability reporting; do not disclose security-sensitive details in public issues.

## License

This repository is source-available for inspection and evaluation, not an OSI-approved open-source release. See [LICENSE](LICENSE).

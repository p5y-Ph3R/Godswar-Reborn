# Client Launcher And Updater Roadmap

Status: future milestone, staged after the base-combat milestone is stable.

## Repository And Product Boundaries

- Use the private repository `p5y-Ph3R/Godswar-Reborn-Client` for client-side
  source, build scripts, patch recipes, manifests, and release automation.
- Build `Godswar.Reborn.Launcher` as the bootstrap and recovery boundary,
  `Godswar.Reborn.Updater` as the manifest and installation engine, and
  `Godswar.NetShim` as the native runtime shim.
- Preserve the deployed compatibility names `patcher.exe` for the updater and
  `Net.dll` for the shim. Do not introduce a self-updating `patch.dll`.
- Preserve the current startup chain during migration:
  `Launch.exe -> patcher.exe autorun -> Origin.exe`. A later launcher can own
  startup while retaining compatibility with existing installations.

## Release And Trust Contract

- Publish immutable, versioned release artifacts with a signed manifest.
- Record each file's target path, version, size, SHA-256 digest, and required
  client/server protocol compatibility in the manifest.
- Verify the manifest signature and every file digest before installation.
  Reject incomplete, altered, unsigned, or incompatible releases.
- Consume explicit release versions. Never update an installed client with
  `git pull`, a mutable `main` branch, or repository working-tree state.

## Atomic Update And Recovery

- Refuse mutation while `Origin.exe` is running.
- Download and verify into a staging directory before touching the live client.
- Replace files as one recoverable transaction, retaining the previous version
  until the new installation passes verification.
- Roll back atomically after an interrupted copy, failed verification, or failed
  activation. The launcher must be able to repair or replace the updater without
  requiring the game process.
- Start `Origin.exe` only after the selected release is fully installed and its
  protocol contract is compatible with the configured server.

## Distribution And Checkout Safety

- Do not commit or publish proprietary stock client binaries or assets. The
  repository may carry only project-owned source and legally distributable
  release inputs or deltas.
- Move the development Git checkout out of the installed game directory. Treat
  the live client directory as a deployment target, not a source checkout.
- Keep release signing keys outside the repository and limit release publication
  to the trusted build path.

This milestone begins only after base combat has a stable, tested protocol and
runtime boundary, so updater compatibility can be defined against a dependable
server release rather than a moving combat implementation.

# Org explorer

A private, offline-first Windows org explorer with rich person profiles, person
notes, manager-owned team context, and observed reporting history.

The agreed first release targets Windows 11 x64 using .NET 10, WPF, and Velopack.
People, notes, photos, and history live in readable local files. An external
provider such as OneDrive owns synchronization and conflict resolution.

## Specifications

- [Agreed v1 design](docs/design.md)
- [Domain vocabulary](GLOSSARY.md)
- [Import contract](docs/import-format.md)
- [Machine-readable import schema](schemas/import-v1.schema.json)
- [Example import files](docs/examples/)
- [Architectural decisions](docs/adr/README.md)
- [Installer, updater, and signing research](docs/research/installer-updater.md)

These are documentation and specifications, not a working application. Application
code, CI/CD workflows, installer artifacts, signing keys, and GitHub enforcement
settings have not been created or configured by this work.

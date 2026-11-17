# Storage Provider Setup

ReStore supports multiple storage backends. Below are links to detailed instructions for setting up each provider.

- [Local Storage](providers/local.md)
- [Google Drive](providers/google-drive.md)
- [AWS S3](providers/aws-s3.md)
- [GitHub Storage](providers/github.md)
- [Azure Blob Storage](providers/azure.md)
- [Google Cloud Storage (GCP)](providers/google-cloud-storage.md)
- [Dropbox](providers/dropbox.md)
- [SFTP](providers/sftp.md)
- [Backblaze B2](providers/backblaze-b2.md)

## Using Multiple Storage Providers

You can configure multiple storage sources in the same `config.json` file. ReStore supports all storage providers simultaneously with flexible per-path and per-component routing.

See [Multiple Storage Providers](providers/multiple-providers.md) for configuration details.

## The `prefix` option

Every provider accepts an optional `prefix` in its `options` (for example `"prefix": "my-laptop"`). ReStore prepends it to every remote path, so several machines or repositories can share one bucket, container or account. It is empty by default, must not start with `/` or contain `..`, and is validated by the configuration checker.

Remote providers ignore the legacy top-level `"path"` setting (only Local storage uses it as its root). If a remote source has `path` set, ReStore logs a one-time warning pointing to `prefix`; the value is not honoured.

## Provider capabilities

Providers implement a common contract (cancellable operations, stream upload, object size lookup, paged listing, a concurrency hint). A provider-independent conformance test suite (`ReStore.Tests/StorageConformanceTests.cs`) pins this behaviour for Local storage and an in-memory fake. To run it against a real provider, set `RESTORE_CONFORMANCE_TYPE` (for example `s3`) and `RESTORE_CONFORMANCE_OPTIONS` (the provider's options as a JSON object) before `dotnet test`; the tests write only under a unique `conf-*` folder.

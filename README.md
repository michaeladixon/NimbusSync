# NimbusSync

Windows-native bidirectional sync for Google Drive and Shared Drives, built in C# / .NET 10. Fills the gap Google Drive for Desktop leaves in Workspace deployments.

## Layout

```
NimbusSync.Engine/   Provider-agnostic sync core (no Google dependency)
  Abstractions/      ICloudProvider, ISyncStateStore, IFileSystemMonitor
  Models/            SyncProfile, SyncItem, SyncConflict, PendingOperation
  State/             SQLite + Dapper store (per-call connections, WAL, outbox table)
  Sync/              SyncEngine, DebouncingFileSystemMonitor, ExclusionMatcher, FileHasher, PathNormalizer
NimbusSync.App/      WPF tray app (Windows). Cloud/GoogleDriveProvider.cs is the Drive v3 adapter.
NimbusSync.Tests/    xUnit. Fakes/ holds an in-memory cloud with a Drive-like change feed.
```

## How a sync cycle works

1. **Detect.** Drain debounced local watcher events; pull remote changes via `changes.list` from the stored page token. If the watcher overflowed, diff the whole local tree against state instead.
2. **Verify.** Every local change is hashed and compared to the item's `LastSyncedLocalHash`; every remote change's `md5Checksum` is compared to `LastSyncedRemoteHash`. Matches are echoes of the engine's own writes and are dropped. This is what prevents ping-pong.
3. **Plan.** Changed-on-one-side becomes an upload or download. Changed-on-both becomes a conflict and the item is frozen until the user resolves it. Local delete + remote edit: remote wins. Local create matching a same-batch local delete by hash: a rename, sent as a metadata move rather than a re-upload.
4. **Commit.** Item baselines, planned operations, and the new change token are written in one SQLite transaction. The token never advances past unrecorded work.
5. **Execute.** Operations run from the outbox with exponential backoff. Downloads go to a `.nimbus-tmp` file, are hash-verified, and then atomically moved into place. A download refuses to overwrite a local file that has drifted from its baseline and raises a conflict instead.

## Safety rules

- Remote **trashed** → local file moves to `.nimbus-trash/<timestamp>/`. Nothing is deleted.
- Remote **removed** (permanent delete, access revoked, or moved out of the synced folder: the API cannot tell these apart) → local file is kept and dropped from tracking.
- Local delete → remote file is **trashed**, never permanently deleted.
- Google-native files (Docs/Sheets/Slides) have no binary content. Default policy writes a `.url` shortcut; edits to stubs are never uploaded. `NativeFilePolicy.Skip` ignores them.

## Shared Drives

`SyncProfile.SharedDriveId` must be set and `RemoteFolderId` is the drive ID (or a folder inside it). The provider sets `supportsAllDrives` on every `files.*` call, `corpora=drive` + `driveId` on listing, and `driveId` on the changes calls, all per the v3 reference.

## Build and test

```bash
dotnet build            # Engine + Tests + App (App compiles cross-platform via EnableWindowsTargeting)
dotnet test             # 70 tests; the one real-OS-watcher test self-skips off Windows
```

## Google Cloud setup

1. Create a project at console.cloud.google.com and enable the Drive API.
2. OAuth consent screen → create a **Desktop app** OAuth client.
3. Pass client ID/secret to `GoogleDriveProvider.CreateAsync`; the refresh token is stored via `FileDataStore`.

## Known limitations (v1)

- A renamed file is detected only when the delete and create land in the same cycle; otherwise it re-uploads.
- `EnsureRemoteFolder` creates a folder without first checking for an untracked same-named remote folder.
- Google-native files are link stubs only; no export to Office formats.
- Engine events fire on background threads; the WPF layer must marshal to the dispatcher.
- 403 `userRateLimitExceeded` relies on the outbox backoff rather than header-driven throttling.

## Next

- WPF tray shell: NotifyIcon, settings, conflict list bound to `ConflictDetected`.
- OAuth wiring and profile creation UI.
- Drive folder picker for `RemoteFolderId` / `SharedDriveId`.

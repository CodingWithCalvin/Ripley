# Ripley

A Windows desktop app for **MakeMKV → HandBrake → Scout**, with a forest-green shell that matches Scout and a border-collie mascot. Rip one disc while previously ripped titles compress in the background. Raw files are retained.

## Quick start

1. Open `artifacts\app\Ripley.exe` after building.
2. Click **New Ripley** under the dog logo to create a disc session in history, then insert and inspect the disc. Drives refresh automatically when the app opens and when Windows reports disc insertion, removal, or drive connection changes. Search TheTVDB, select the series, and set the raw folder format and filename prefix. Ripley reads Scout's saved TVDB credentials without copying them into its own settings or logs.
3. Click **Rip disc**. Raw files and metadata are saved independently of compression, with no HandBrake configuration required.
4. When the disc finishes, optionally choose **Add to queue**. This adds only that disc's completed files using your compression settings. Ripping never automatically grows a compression queue.
5. On **Compression Queue → Output & presets**, set the compressed output folder, folder format, filename prefix, installed HandBrake preset, and hardware decoding. Choose **Save settings**. Use **Add existing rips** to select finished folders, preview the names, and add files.
6. Files are grouped into disc queues. Choose **Start selected queue** or **Start all queues**; Start all finishes each queue before moving to the next. You can rip more discs while compression runs.

Adding files while idle never starts compression. When compression is running, the add action explicitly says **Add to running queue** and includes those files in the active run. While paused or pausing after a file, new additions wait for the next explicit start.

**Pause after current file** lets the active encode finish and save before pausing. Choose **Keep processing** to cancel that pending pause. **Stop all…** interrupts the active rip and encode after confirmation: raw and completed files are kept, the incomplete compressed file is discarded, and pending jobs remain paused. An interrupted disc must be ripped again.

The minimum title length defaults to **120 seconds**, passed to MakeMKV as `--minlength=120`. All qualifying titles are ripped. Inspection shows titles and tracks before ripping; it does not identify episodes.

Automatic drive refresh waits briefly for media changes to settle and runs in the background using Windows drive information. It keeps empty optical drives listed, preserves the selected drive, and updates disc-label hints in a draft. Events received during ripping or inspection wait until that operation finishes. Saved history stays unchanged, and inserting a disc does not start a rip or add compression jobs. **Refresh** remains available for manual checks, including environments where Windows does not deliver media notifications (such as Remote Desktop). Closing an empty tray does not generate a ready-disc event.

## Folder formats and filenames

| Token | Example |
| --- | --- |
| `{series}` or `{series_name}` | `iCarly` |
| `{year}` | `2007` |
| `{tvdb_id}` | `82493` |
| `{season}` | `1` |
| `{season:00}` | `01` |
| `{volume_number}` / `{volume_number:00}` | `2` / `02` from `V2` |
| `{disc_number}` / `{disc_number:00}` | `2` / `02` from `D2` |
| `{disc_label}` | `ICARLY_S1_V1_D1` |
| `{date}` | `2026-11-06` |
| `{date:yyyyMMdd}` | `20261106` |

**Auto from disc label** fills the editable **Season**, **Volume number**, and **Disc number** fields from explicit markers. `ICARLY_S1_V2_D2` means season 1, volume 2, disc 2. Fill in missing values or override detected values for the current disc. With auto detection enabled, selecting another disc clears values that cannot be detected; disc number alone is never mapped to a season. Disable auto detection to keep manual values between discs. `{disc_label}` is the full label; `{disc_number}` is only its disc number. The token names are `{disc_label}`, `{disc_number}`, and `{volume_number}`; `{disc}`, `{disc_desc}`, and `{volume}` are not supported.

Use `\` to create subfolders. Use `{{` and `}}` for literal braces. Token names are case-insensitive. Missing values and unknown tokens are errors; choose the series, supply the missing disc values, or remove those tokens. For workflows without a series, `{disc_label}` is sufficient.

Default raw format:

```text
{series} ({year}) [tvdb-{tvdb_id}]\{disc_label}
```

Default compressed format:

```text
{series} ({year}) [tvdb-{tvdb_id}]
```

Example compressed format with a season folder:

```text
{series} ({year}) [tvdb-{tvdb_id}]\Season {season:00}
```

This expands to `iCarly (2007) [tvdb-82493]\Season 01` under the selected base folder. Raw and compressed folders are reused exactly as the format specifies, so multiple discs can share a season folder. Filename collisions use numbered suffixes such as ` (2)`; existing files are never overwritten. Metadata is sanitized for Windows filenames, and templates cannot escape their base folder. Each queued batch keeps its resolved destination even if the form is later changed.

Set **Filename prefix** to:

```text
{volume_number}_{disc_number}_
```

With volume 2, disc 2, and MakeMKV filename `ICARLY_S1_t00.mkv`, Ripley writes `2_2_ICARLY_S1_t00.mkv`. The folder format `{series} {{tvdb-{tvdb_id}}}\Season {season:00}` places it in `iCarly {tvdb-82493}\Season 01` under each chosen base folder. Compressed output keeps the prefixed name, using the container extension from your preset. Padded numbers also work, for example `{volume_number:00}_{disc_number:00}_`. A blank prefix preserves MakeMKV’s filename. The prefix supports the same tokens as folder formats, but cannot contain subfolders.

Ripley applies filenames after MakeMKV confirms a complete rip and before compression is queued. Each disc has a separate `.ripley-rips\<session>` folder for its log, metadata, and incomplete files. Completed raw MKVs are moved into the requested folder without overwriting existing files. The manifest records the original and final filenames; compressed sidecars also retain the original filename. Interrupted rips stay in their session folder and are excluded from compression imports.

**Compress** evaluates the compressed folder format separately for every file. A batch may contain several discs, seasons, or series, even in one shared raw folder: each file uses its saved series identity, season, volume, disc number, label, and rip date. Those values also stay with its queue entry and compressed sidecar. The series and numeric fields for the currently inserted disc are not applied to the batch.

For older rips, explicit markers in source folder names, filenames, and folders within the selected tree can supply the season, volume, and disc number. The source folder name supplies the label when no saved label exists. If series metadata is missing, explicitly enable **Use the selected series for files without saved series metadata**; saved series identities always take priority. Compress has its own optional TVDB series search; it does not change the series selected on Rip. Missing token values identify the affected source and prevent the batch from being queued. `{date}` uses the saved rip date, an older manifest's completion date, or the source file's modification date.

Compress owns its compressed base folder, folder format, and external-rip filename prefix. The raw filename prefix on Rip is independent. **Preview names** shows each source and its resolved output. External raw files are kept unchanged; the prefix applies to their compressed filenames. Ripley’s own completed rips keep their existing prefix when reimported.

## Presets and hardware

Ripley loads `%APPDATA%\HandBrake\presets.json` by default and labels custom presets. It snapshots the selected preset under a unique imported name for each batch, retaining its encoder, quality, container, audio, subtitles and filters. Editing the GUI preset later does not change queued jobs.

Hardware decoding can follow the installed GUI settings or explicitly use software, NVIDIA NVDEC, or Intel QSV. Availability is checked when adding files to the compression queue. Ripping does not require HandBrake. Actual decode support also depends on the source format and driver. Hardware encoding is selected by the preset: `x265_10bit` is CPU encoding; an NVENC preset uses NVIDIA encoding. Ripley does not automatically replace one encoder with another.

## Navigation and queue controls

The shell follows Scout, with a custom title bar, green sidebar, **New Ripley**, saved rip history, and **Compression Queue** in the sidebar, and a persistent activity footer. **Settings** holds executable locations, the HandBrake preset-file path, GUI hardware-settings path, and Scout data folder. Preset and decoder choices live in Compress.

Each rip history entry includes a file queue with current-file and whole-disc progress, filenames, counts, and bytes written. The count follows MakeMKV's actual save operation. Filenames appear as files are created and update after final naming. **Written** means MakeMKV moved to another file; **Saved** means the full rip and raw-file publication succeeded. Cancelled or failed rips distinguish retained, unverified files from files never started.

**Compression Queue** has a list of disc queues and an **All queues** view. Ripley manifests group shared-folder files by their original disc session; external rips without manifests are grouped by source folder. The queue list, selected settings, file identities, and destinations persist. Existing older queues reopen under **Saved queue**. Restoring the app never starts processing automatically.

Each file shows its full **Input** and **Output** path. Long paths wrap and can be selected and copied. Output paths reflect the queue's saved destination and update if a filename collision requires a numbered suffix.

Compression Queue has visible **Queues**, **Add existing rips**, and **Output & presets** tabs. Folder, naming, preset, and hardware settings can be edited and saved without adding files. The source tab summarizes the current output settings and links directly to the editor. Adding files returns to the queue and confirms added or duplicate entries. Each batch snapshots its preset and resolved destinations, so later settings changes do not alter existing jobs.

**Clear queue** removes all queue entries without deleting raw or completed media. If encoding is active, confirmation explains that the current encode will be cancelled and its incomplete output discarded. A disc operation continues; files explicitly added during cleanup are retained and wait. **Cancel disc operation** stops just the disc, while **Stop all…** stops both workflows.

Rip sessions are saved in `%LOCALAPPDATA%\Ripley\history.json`. **New Ripley** creates a new draft while retaining folder preferences and the selected series; it clears disc-specific values for the next disc. Selecting history restores that session's settings, file progress, and completed raw files. Completed entries can be added to compression later without reripping. During a disc operation, you can navigate between compression and the active history entry; creating or switching to another session waits until that operation finishes. Interrupted sessions reopen as interrupted and never restart automatically. The header no longer duplicates the sidebar navigation.

## Upgrading from Reel

On first launch, Ripley copies saved settings, the queue, and frozen presets from `%LOCALAPPDATA%\Reel` to `%LOCALAPPDATA%\Ripley`. The original data is kept, preset paths are updated, and unfinished jobs reopen paused. Existing Ripley settings are never replaced. Close Reel before opening Ripley; the applications cannot run competing queues.

Existing Reel raw manifests and sidecars remain readable. Newly generated metadata uses `ripley-disc.json`, `.ripley-rips`, and `.ripley.json`; existing media files and metadata are not renamed.

## Recovery and Scout handoff

- Pausing compression does not cancel ripping, and cancelling ripping does not cancel compression.
- Incomplete/cancelled rips remain on disk and are not automatically queued.
- Completed encodes are finalized without replacing existing files; temporary incomplete encodes are cleaned up.
- The queue is saved in `%LOCALAPPDATA%\Ripley\queue.json`; interrupted jobs reopen paused and can be resumed/retried. A job interrupted immediately during final publication may produce a numbered duplicate on retry rather than assuming an existing file belongs to that job.
- Each raw disc session under `.ripley-rips` contains `ripley-disc.json` and `makemkv.log`. Compressed files have `.ripley.json` sidecars recording series identity, source, preset and disc-metadata location.
- Scout still performs episode identification and final naming. Automatic import of Ripley's sidecars into Scout is not implemented.

## Build and test

Requires Windows, the **.NET 9 SDK**, and the **.NET 9 Windows Desktop Runtime** to run the framework-dependent build. MakeMKV and HandBrakeCLI are separate installed tools. A CLI compatible with the GUI preset version is required.

Open `CodingWithCalvin.Ripley.slnx`, or use:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Build scripts run from the repository root, regardless of the calling directory. The executable is published to `artifacts\app\Ripley.exe`; build and intermediate files are stored under `artifacts\bin` and `artifacts\obj`. The entire `artifacts` folder is ignored by Git.

Optionally bundle a locally installed CLI into `artifacts\app\handbrake`:

```powershell
.\scripts\build.ps1 -HandBrakeCli 'C:\path\to\HandBrakeCLI.exe'
```

The test script builds the SLNX solution and a fake process to exercise ripping success, partial failure, cancellation, and concurrent compression without accessing a physical disc. Tests also cover collisions, source changes, preset snapshots, metadata parsing, queue recovery, folder templates, filename prefixes, shared raw folders, and interrupted filename publication. Additional checks cover saved-data migration, desktop navigation, clearing an active queue while a disc completes, ripping without HandBrake, later import of raw-only rips, and queue confirmations with paused and running compression. Desktop checks create no visible windows and use temporary state. Live TVDB search and a real short HandBrake encode were verified separately; a complete physical-disc rip remains to be validated.

Source is under `src\Ripley`; checks and the fake runtime are under `tests`. Build output, third-party runtime binaries, logs, and credentials are excluded from Git. Third-party executables retain their own licenses. MakeMKV documentation: https://www.makemkv.com/developers/usage.txt. TVDB search is powered by TheTVDB: https://thetvdb.com/.

The Ripley mascot was created with the built-in image-generation tool using Scout’s icon as the style reference. Source artwork, Windows icon sizes, and the generation prompt are in `src\Ripley\Assets`.

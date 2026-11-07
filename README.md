# Reel

A Windows desktop app for **MakeMKV → HandBrake → Scout**. Rip one disc while previously ripped titles compress in the background. Raw files are retained.

## Quick start

1. Open `artifacts\app\Reel.exe` after building.
2. On **Rip a disc**, refresh drives and inspect the inserted disc.
3. Search TheTVDB and select the correct series/year. Reel reads the TVDB key and optional PIN saved by Scout; it does not copy credentials into its settings or logs.
4. Choose raw and compressed **base folders**, then edit their folder formats. Live previews show where files will go.
5. On **Preset & tools**, select a preset from the installed HandBrake GUI or an exported preset JSON. Check the preset and hardware.
6. Click **Rip + queue compression**. When the rip finishes, insert another disc and refresh. Compression continues independently.

The minimum title length defaults to **120 seconds**, passed to MakeMKV as `--minlength=120`. All qualifying titles are ripped. Inspection shows titles and tracks before ripping; it does not identify episodes.

## Folder formats

| Token | Example |
| --- | --- |
| `{series}` or `{series_name}` | `iCarly` |
| `{year}` | `2007` |
| `{tvdb_id}` | `82493` |
| `{season}` | `1` |
| `{season:00}` | `01` |
| `{volume}` / `{volume:00}` | `2` / `02` from `V2` |
| `{disc_number}` / `{disc_number:00}` | `2` / `02` from `D2` |
| `{disc}` or `{disc_label}` | `ICARLY_S1_V1_D1` |
| `{date}` | `2026-11-06` |
| `{date:yyyyMMdd}` | `20261106` |

**Auto season from disc label** recognizes explicit markers such as `S1` or `SEASON_1`. `ICARLY_S1_V2_D2` means season 1, volume 2, disc 2. A new label with `S2` updates the season and folder preview. An ambiguous/new label clears the previous season and asks for a manual value; disc number alone is never mapped to a season. You can edit the season for the current disc or disable auto detection. `{volume}` and `{disc_number}` require explicit markers in the label.

Use `\` to create subfolders. Use `{{` and `}}` for literal braces. Token names are case-insensitive. Missing values and unknown tokens are errors; choose the series, supply the season/label, or remove those tokens. For workflows without a series, `{disc}` is sufficient.

Default raw format:

```text
{series} ({year}) [tvdb-{tvdb_id}]\{disc}
```

Default compressed format:

```text
{series} ({year}) [tvdb-{tvdb_id}]
```

Example compressed format with a season folder:

```text
{series} ({year}) [tvdb-{tvdb_id}]\Season {season:00}
```

This expands to `iCarly (2007) [tvdb-82493]\Season 01` under the selected base folder. Raw folders use numbered suffixes when they already exist. Compressed folders may collect multiple discs; existing files are never overwritten. Metadata is sanitized for Windows filenames, and templates cannot escape their base folder. Each queued batch keeps its resolved destination even if the form is later changed.

**Existing rips** also uses the compressed folder format. For `{disc}`, each source file's containing directory is used, or the saved disc label from `reel-disc.json` when available.

## Presets and hardware

Reel loads `%APPDATA%\HandBrake\presets.json` by default and labels custom presets. It snapshots the selected preset under a unique imported name for each batch, retaining its encoder, quality, container, audio, subtitles and filters. Editing the GUI preset later does not change queued jobs.

Hardware decoding can follow the installed GUI settings or explicitly use software, NVIDIA NVDEC, or Intel QSV. Availability is checked before ripping/queueing. Actual decode support also depends on the source format and driver. Hardware encoding is selected by the preset: `x265_10bit` is CPU encoding; an NVENC preset uses NVIDIA encoding. Reel does not automatically replace one encoder with another.

## Recovery and Scout handoff

- Pausing compression does not cancel ripping, and cancelling ripping does not cancel compression.
- Incomplete/cancelled rips remain on disk and are not automatically queued.
- Completed encodes are finalized without replacing existing files; temporary incomplete encodes are cleaned up.
- The queue is saved in `%LOCALAPPDATA%\Reel\queue.json`; interrupted jobs reopen paused and can be resumed/retried. A job interrupted immediately during final publication may produce a numbered duplicate on retry rather than assuming an existing file belongs to that job.
- Raw disc folders contain `reel-disc.json` and `makemkv.log`. Compressed files have `.reel.json` sidecars recording series identity, source, preset and disc-metadata location.
- Scout still performs episode identification and final naming. Automatic import of Reel's sidecars into Scout is not implemented.

## Build and test

Requires Windows, the **.NET 9 SDK**, and the **.NET 9 Windows Desktop Runtime** to run the framework-dependent build. MakeMKV and HandBrakeCLI are separate installed tools. A CLI compatible with the GUI preset version is required.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Build scripts run from the repository root, regardless of the calling directory. The executable is published to `artifacts\app\Reel.exe`; build and intermediate files are stored under `artifacts\bin` and `artifacts\obj`. The entire `artifacts` folder is ignored by Git.

Optionally bundle a locally installed CLI into `artifacts\app\handbrake`:

```powershell
.\scripts\build.ps1 -HandBrakeCli 'C:\path\to\HandBrakeCLI.exe'
```

The test script builds a fake process to exercise ripping success, partial failure, cancellation, and concurrent compression without accessing a physical disc. Tests also cover collisions, source changes, preset snapshots, metadata parsing, queue recovery, and folder templates. Live TVDB search and a real short HandBrake encode were verified separately; a complete physical-disc rip remains to be validated.

Source is under `src\Reel`; checks and the fake runtime are under `tests`. Build output, third-party runtime binaries, logs, and credentials are excluded from Git. Third-party executables retain their own licenses. MakeMKV documentation: https://www.makemkv.com/developers/usage.txt. TVDB search is powered by TheTVDB: https://thetvdb.com/.

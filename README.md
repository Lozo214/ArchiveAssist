# Archive Assist

Archive Assist is a C# WPF rebuild of the Python PDF Workflow Assistant.

## Current milestone

- Select multiple files, multiple folders, or any combination of both. Drop them directly onto the
  main window, reopen recent locations, and preview selected folders recursively. Overlapping
  selections are counted only once.
- Classify PDFs, supported photos, `_back` and `_b` photo backs, and unsupported/skipped files.
- Ignore generated `Equalized PDFs` folders and continue past inaccessible files or folders.
- Scan on a background thread with phased status, elapsed time, live progress, cancellation, retained
  partial results, and a non-modal completion banner.
- Choose Fast Count Only, Standard QA, or Deep OCR Check for searchable-text preflight.
- Sample a spread of pages in Standard QA, or inspect every page and report exact non-OCR page
  numbers in Deep OCR Check. Searchability is inferred from extractable PDF text.
- Select flagged Report rows and create searchable copies with OCRmyPDF and Tesseract. Archive Assist
  checks the OCR setup, requires a separate output folder, preserves source subfolders, skips existing
  outputs, supports cancellation, and verifies page counts and text layers before keeping each result.
- Open standalone Options-menu tools to force-OCR entire PDFs in place or optimize PDFs in place.
  Both accept multiple PDF files and folders, use verified same-folder temporary output, and show
  per-file results. Optimization defaults to lossless and keeps the original unless the output is smaller.
  Every successful replacement retains its original in the managed Recovery Center.
- Count PDF pages as Documents or Maps using configurable physical-size thresholds.
- Flag oversized scans and PDFs over a configurable page limit.
- Search, sort, and filter the Report grid with quick views for warnings, non-OCR files, large scans,
  and PDF errors. Copy selected rows (or all visible rows) as tab-delimited `File Name`, `Documents`,
  `Maps`, `Photos`, `Photo Backs`, and `Total` columns for Excel.
- Move automatically from Discovery to Report when a scan completes, and return to Discovery when
  the selected files or folders change. A compact centered count strip identifies which selection
  its totals describe.
- Remember Report search/filter state, column widths, and sorting. Preferences can include or omit
  clipboard headers and reset the saved Report layout.
- Review the Python-style Summary panel and double-click actionable metrics to open matching Report rows.
- Browse a warning-highlighted File Structure tree with Documents, Maps, and Photos rolled up by
  folder. Fixed column guides keep nested values aligned with their headers.
- Double-click files for document metadata and scan details, or use context menus to open paths,
  copy names, and rename files or folders. Press `F2` to rename the selected item; Archive Assist
  then refreshes Discovery so stale scan results are not shown.
- Show a concise non-modal completion summary after each successful scan, with shortcuts to the
  Report, warnings, and Summary.
- Preview PDF page equalization before writing, including source files/pages, over-limit files,
  affected folders, and expected output PDFs.
- Rebuild only folders that need equalization, preserve alphabetical page order and source subfolders,
  enforce the configured page limit, and leave every original PDF untouched.
- Write output safely through `.part` files, remove partial output after failure or cancellation, and create
  `equalization_manifest.csv` as an automatic page-to-source audit trail.
- Remember recent scan locations, the last picker location, scan defaults, the expanded/collapsed
  advanced panel, Report layout, and window size/position.
- Open PDFs in the built-in editor from File, Report, or File Structure. The editor provides a
  thumbnail grid for batch work and a detailed page view with fit, zoom, navigation, and panning.
- Rotate, crop, delete, and drag selected pages to reorder them, with bounded undo/redo history.
  Saving safely updates the opened PDF after retaining its original in the managed Recovery Center.
- Restore the original editing-session recovery point from the editor. Before restoration, Archive
  Assist retains the currently saved edited version as another recovery point.
- Reopen up to eight recently used PDFs from the main window or editor, and show dismissible
  first-use guidance for the editor workflow.
- Keep large PDFs responsive by recycling off-screen thumbnail cards, rendering only pages near the
  current viewport, and evicting older thumbnail images from a bounded memory cache.
- Use the top File, Options, View, and Help menus for archive commands, Preferences, the
  self-contained PDF Page Equalizer, experimental PDF processing, and application information.

## Main-window workflow

Archive Assist guides the primary task as `Select → Configure → Scan → Review → Act`.

1. Drop files and folders onto the selection area, choose `Select files/folders...`, or reopen a
   location from `Recent locations`. Adding or changing a selection automatically opens Discovery
   so the files about to be scanned are visible.
2. Leave the advanced scan settings collapsed for the usual workflow, or expand them to change the
   map threshold, PDF page limit, and QA mode.
3. Start the scan and follow its current phase, elapsed time, and file progress. A completed scan
   automatically opens Report.
4. Choose a view from `Show`, search the Report, use a quick filter, open a file or folder, or copy
   the visible rows into Excel.
5. Use `Options > Preferences...` to change scan defaults, Report copy/layout behavior, editor
   defaults, and first-use guidance.

The centered count strip beneath the tabs identifies the selected file, folder, or group of
locations represented by its totals. Its compact cards show Documents, Maps, Photos, Photo Backs,
Production Total, and Warnings without spanning the full window.

The main window includes dismissible first-use guidance, descriptive empty states, keyboard
shortcuts, and concise status notifications. Press `F5` to scan, `Ctrl+F` to search the Report,
and `Ctrl+C` to copy selected Report rows. Use `Ctrl+1`, `Ctrl+2`, and `Ctrl+3` to open Discovery,
Report, and File Structure.

### File Structure navigation and renaming

File Structure preserves the folder hierarchy while keeping Type, Documents, Maps, Photos, and
Warnings aligned with fixed headers and vertical column guides. Select an item and press `F2`, or
choose `Rename...` from its context menu, to rename it without leaving Archive Assist.

Rename validation blocks invalid Windows names and existing-name collisions. Folder renames include
an extra confirmation because every contained path changes. After a successful rename, Archive
Assist rediscovers the current selection, clears stale scan results, and returns to Discovery.

## Safe in-place saving and Recovery Center

Archive Assist normally keeps users working with the files they selected:

- PDF Editor saves update the open PDF in place after securing its original version.
- Verified optimization and whole-file OCR replacements use the same recovery system.
- Source folders do not receive visible backup folders or timestamped backup copies.
- Scanning remains read-only and does not create recovery points.
- Page Equalizer continues to create a separate output set because it may combine and split many
  source PDFs. Its results remain accessible directly from the equalizer workflow.

Open `File > Recovery Center...` to review recovery points, restore an earlier version, open the
current file, or delete a recovery point. Restoring a file first retains its current version, making
the restore itself reversible.

`Options > Preferences > File Safety` provides three PDF Editor save modes:

- `Safe in-place (recommended)` updates the open PDF and keeps a managed recovery point.
- `Ask each time` chooses between safe in-place and an edited copy on every save.
- `Save edited copies` keeps the original unchanged and continues editing the newly saved copy.

Recovery points are stored under Archive Assist's private local application data rather than beside
source documents. Retention can be set to 7, 30, or 90 days, or kept until manually deleted.
Expired points are cleaned when the application starts and can also be cleaned from Recovery Center.

The default threshold is `12 x 18 (Standard Scan Size)`. Its special feeder rule allows a long page
when its narrow dimension is 12 inches or less. Existing installations are migrated to this default
once; later user selections continue to be remembered.

## PDF editor

Open `File > Open PDF in Editor...`, or choose `Edit PDF...` from a PDF row in the Report or File
Structure views.

1. Use `Thumbnail Grid` to select one or more pages with Ctrl+click or Shift+click.
2. Rotate, crop, or delete the selection, or drag it before/after another page to reorder it.
3. Use `Page View` for close inspection, fit-to-page or fit-to-width, zooming, and click-drag
   panning. Its navigation and zoom controls sit below the page so they remain visually separate
   from the editor commands.
4. Use Ctrl+Z/Ctrl+Y to undo or redo edits, then Ctrl+S to save.
5. After saving, use `Restore Original...` if needed. The currently saved edited PDF is retained as
   another managed recovery point before the original is restored.
6. Choose `Close and Rescan` to refresh the main production report after a saved edit.

The editor keeps at most 20 history entries and bounds history memory for very large PDFs. Editing and
saving remain available when a document is too large for another undo snapshot. Its thumbnail grid
also virtualizes page cards and retains only a bounded set of recently viewed page images, so opening
a long PDF does not render and hold every thumbnail at once.

## QA scan modes

| Mode | Behavior |
| --- | --- |
| Fast Count Only | Skips searchable-text checks for the quickest production count. |
| Standard QA | Samples the first pages plus the middle and last page, stopping when text is found. Results are labeled as likely searchable or likely non-searchable. |
| Deep OCR Check | Checks every page and records exact page numbers without extractable text. |

## Experimental OCR and optimization

OCR and optimization remain available for evaluation, but further OCR development is paused while
performance and recognition quality are reassessed. These commands are grouped under
`Options > Experimental PDF Processing` and are not part of the recommended production workflow.

### Creating searchable copies

1. Run a Standard QA or Deep OCR Check scan.
2. Filter the Report to `Non-OCR files`.
3. Select the PDFs to process. Use Ctrl+A to select all visible rows if appropriate.
4. Choose `Create searchable copies...`.
5. Confirm that OCRmyPDF and Tesseract show `Ready`. Ghostscript is optional for this workflow.
6. Choose which pages to OCR:
   - `Only pages without text (recommended)` copies text-bearing pages unchanged and OCRs only pages
     without searchable text.
   - `All pages (force OCR)` rasterizes and OCRs every page. Use it only to replace an incorrect
     existing text layer.
7. Choose an existing output folder that is separate from the source archive.
8. Start OCR and review the Results tab.

## Page equalizer

Open `Options > PDF Page Equalizer...`. The equalizer window contains the complete workflow:

1. Select the source folder.
2. Enter the maximum pages per output PDF.
3. Preview the folder-by-folder plan.
4. Choose the output location and create the equalized PDFs.

The main scan window does not need an active archive folder before opening the equalizer.

OCR output is non-destructive:

- Source PDFs are only read and are never overwritten.
- Output filenames and relative subfolders match the source archive.
- Existing destination files are skipped.
- Each PDF is written to a unique partial filename and moved into place only after OCR succeeds and
  the output page count is verified.
- A completed-with-warning result means OCR succeeded but one or more pages still have no extractable
  text, which can be normal for truly blank pages or pages without recognizable lettering.

## In-place OCR and optimization

Open either `Options > OCR entire PDFs in place...` or `Options > Optimize PDFs...`. These standalone
tools do not require a completed archive scan.

1. Select any combination of PDF files and folders. Folders are searched recursively.
2. Confirm that the required OCRmyPDF components show `Ready`.
3. For optimization, choose Level 1 lossless (the default), Level 2 balanced, or Level 3 aggressive.
4. Review the queue and start processing.
5. Read and accept the in-place replacement warning, then review the Results tab. Use `Undo last`
   immediately after a completed batch or open Recovery Center for any retained original.

The whole-file OCR tool force-OCRs every page and can flatten forms, signatures, bookmarks, or other
interactive content. The optimization tool disables OCR and uses OCRmyPDF's optimizer. Both create a
unique temporary PDF beside the original, verify that it opens and has the same page count, and only
then retain a managed recovery point and replace the original. Optimization keeps the original when
the verified result is not smaller.
Cancellation removes the current temporary output; files already completed remain completed. While
OCRmyPDF is active, the progress bar animates until page information becomes available, then advances
within the current file. The window also shows elapsed time and the latest useful OCRmyPDF activity.

### Windows OCR prerequisites

Archive Assist detects these components automatically:

- OCRmyPDF 17 or later: `python -m pip install ocrmypdf`
- 64-bit Tesseract OCR with English language data
- 64-bit Ghostscript is detected but optional for Archive Assist's standard PDF output. It would be
  required for a future explicit PDF/A conversion mode.

Use the official OCRmyPDF Windows installation instructions for supported download and installation
options. Reopen Archive Assist or press `Recheck setup` after installing a missing component.

## Structure

- `src/ArchiveAssist.App` - WPF UI, MVVM view model, commands, and mixed file/folder selection window
- `src/ArchiveAssist.Core` - discovery, PDF/text inspection, page-size rules, equalization, and scan models
- `tests/ArchiveAssist.Core.Tests` - scanner and equalizer behavior tests with synthetic PDFs
- `tests/ArchiveAssist.App.Tests` - settings, renderer, recent-file, and WPF construction smoke tests

## Run

```powershell
dotnet run --project src/ArchiveAssist.App -c Release
```

## Test

```powershell
dotnet test -c Release
```

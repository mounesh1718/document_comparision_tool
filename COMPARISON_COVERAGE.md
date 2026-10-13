# Reviewing comparison results

Reference is the approved source. Red indicates Reference content; green indicates Current content. Select a highlighted value or image region to focus the findings list and see both values, structural locations, context, and an image crop where available. Use **Show all changes** to leave the focused selection. Filters separate table, image, missing/added, and review findings.

## What the comparison checks

- Body text, spaces, and direct run/paragraph formatting.
- Headers and footers (default, first-page and even-page, per section), footnotes and endnotes. Headers and footers can be left out with **Include headers & footers** under Compare; they are then neither compared nor shown, and the result says so. Each is compared only with the same part in the other document and shown as a labelled area in the viewer. In headers and footers, page-number, page-count and date/time fields are compared as fields, not as their last printed value, because they print differently on every page. Fields in the body keep their printed value.
- Basic native tables: row/cell values and structure. Unique row labels and column headers help align content after middle rows or columns are removed.
- Embedded images: image changes and recognized text/visual regions. Text in images is read with the computer's built-in OCR: Windows OCR on Windows, Apple Vision on macOS. Linux has no OCR, so image changes are shown as visual changes with crops.
- Image-to-table replacements in unambiguous corresponding sections, in either direction. Spatial OCR row labels and column gaps map recognized values to native table cells; simple single-row content can use reading order.
- SHA-256 equality: **Exact File Match** means identical file bytes. Otherwise, no findings means only that no differences were found within the implemented checks. Author/date/package metadata is excluded from content comparison.

## Two ways to view the documents

- **Reading view** (default): the text of both documents side by side, kept level line by line. Fast, but the layout is simplified (no real page breaks, fonts or column widths).
- **Exact pages**: the real pages, laid out by Microsoft Word (Windows) or LibreOffice (any system, free), with the differences marked on them. It takes a few seconds the first time. The engine works on a temporary copy; copies and laid-out pages are deleted when the app closes.
- **Open in Word** opens a read-only copy of either document in Word.
- **History** lists every comparison made on this computer (when, who, which files with their fingerprints, the result and each difference), can compare the same files again, and exports to CSV. It is stored only on this computer and can be cleared.

## What requires verification

OCR is recognition, not ground truth. It can misread digits, punctuation, symbols, small text, and complex grids. OCR-derived findings are labeled **verify**, even when recognized values match. Their image crops let reviewers inspect the original evidence. Failed or ambiguous recognition never certifies a match.

Mixed-content pairing requires an unambiguous image-only versus table-only section with the same heading (or the single implicit document-body section). Multiple images, renamed/duplicate headings, complex layouts, wrapped cells, and unmatched rows may require manual alignment. Recognized mapped cells are not proof that every image value was extracted. Unmapped rows, prose, and layout need review.

Inherited styles, character styles, list numbering, exact Word pagination/layout, floating-image wrapping/placement, tracked revisions, text boxes, equations, embedded objects and block-level content controls are not fully verified. Detected unsupported structures (including equations) produce review warnings. Merged cells and nested tables are read and compared. Hidden text and inserted symbols (≤ ≥ ±) are compared. Displayed page positions can be estimates; structural paragraph/table/row/cell locations are the reliable navigation references.

This tool does not promise 100% semantic or visual accuracy for arbitrary DOCX files. It is a local, read-only review assistant. Source files are not modified. PDF generation is optional and starts only when explicitly requested; reports retain verification notes.

## Run and verify

Close the existing app, then run `dotnet run` from this project directory to load the updated build.

Run regression tests with `dotnet test tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj` (Windows also accepts backslashes). On Windows and macOS the expected result is 90 passed, 6 skipped (known gaps). Tests include the viewer, image/table recognition, missing rows/columns, failed/ambiguous OCR, and the sample documents in `samples/`. Windows OCR needs an installed supported recognition language; Apple Vision is built into macOS 10.15 and later.

The full requirement list and test cases are in [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) and [docs/TEST_CASES.md](docs/TEST_CASES.md).

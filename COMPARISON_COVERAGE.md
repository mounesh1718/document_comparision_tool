# Reviewing comparison results

Reference is the approved source. Red indicates Reference content; green indicates Current content. Select a highlighted value or image region to focus the findings list and see both values, structural locations, context, and an image crop where available. Use **Show all changes** to leave the focused selection. Filters separate table, image, missing/added, and review findings.

## What the comparison checks

- Body text, spaces, and direct run/paragraph formatting.
- Headers and footers (default, first-page and even-page, per section), footnotes and endnotes. Each is compared only with the same part in the other document and shown as a labelled area in the viewer. In headers and footers, page-number, page-count and date/time fields are compared as fields, not as their last printed value, because they print differently on every page. Fields in the body keep their printed value.
- Basic native tables: row/cell values and structure. Unique row labels and column headers help align content after middle rows or columns are removed.
- Embedded images: image changes and recognized text/visual regions.
- Image-to-table replacements in unambiguous corresponding sections, in either direction. Spatial OCR row labels and column gaps map recognized values to native table cells; simple single-row content can use reading order.
- SHA-256 equality: **Exact File Match** means identical file bytes. Otherwise, no findings means only that no differences were found within the implemented checks. Author/date/package metadata is excluded from content comparison.

## What requires verification

OCR is recognition, not ground truth. It can misread digits, punctuation, symbols, small text, and complex grids. OCR-derived findings are labeled **verify**, even when recognized values match. Their image crops let reviewers inspect the original evidence. Failed or ambiguous recognition never certifies a match.

Mixed-content pairing requires an unambiguous image-only versus table-only section with the same heading (or the single implicit document-body section). Multiple images, renamed/duplicate headings, complex layouts, wrapped cells, and unmatched rows may require manual alignment. Recognized mapped cells are not proof that every image value was extracted. Unmapped rows, prose, and layout need review.

Inherited styles, exact Word pagination/layout, floating-image wrapping/placement, tracked revisions, text boxes, embedded objects, merged cells, and nested tables are not fully verified. Detected unsupported structures produce review warnings. Displayed page positions can be estimates; structural paragraph/table/row/cell locations are the reliable navigation references.

This tool does not promise 100% semantic or visual accuracy for arbitrary DOCX files. It is a local, read-only review assistant. Source files are not modified. PDF generation is optional and starts only when explicitly requested; reports retain verification notes.

## Run and verify

Close the existing app, then run `dotnet run` from this project directory to load the updated build.

Run regression tests with `dotnet test tests\DocumentChecker.Engine.Tests\DocumentChecker.Engine.Tests.csproj`. Tests include native UI highlight selection, image/table recognition, missing rows/columns, and failed/ambiguous OCR. Windows OCR needs an installed supported recognition language.

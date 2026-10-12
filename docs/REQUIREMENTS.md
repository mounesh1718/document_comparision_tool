# Document Checker: Requirements

| | |
|---|---|
| **Product** | Document Checker (Reference Review) |
| **Version described** | Commit `bf00bbf` plus the fixes of 2026-10-12 (hidden text, symbols, equations, locations, report wording) |
| **Companion document** | [TEST_CASES.md](TEST_CASES.md). Every requirement below lists the test cases that verify it. |

**Status key used in this document**

| Status | Meaning |
|---|---|
| ✅ Met | Implemented and covered by at least one automated test. |
| 🟡 Partial | Implemented with known limits, or implemented but not covered by an automated test. |
| ❌ Gap | Not implemented today. |
| 🐞 Defect | Implemented, but behaves incorrectly in a known case. |

---

## 1. Purpose

Document Checker compares a **Current** Word document (`.docx`) against an approved **Reference** Word document and shows the reviewer every difference it can detect, side by side.

It is a **review aid**. It helps a reviewer find differences quickly and see the evidence for each one. It does **not** certify that two documents are equivalent. Only an identical file (same bytes) is reported as an exact match.

## 2. Users

| User | Goal |
|---|---|
| **Reviewer** (QA, document control, regulatory) | Confirm that Current still matches the approved Reference, and find every deviation with its location. |
| **Author** | Fix deviations in Current and re-check immediately. |
| **Approver / auditor** | Read a PDF record of what was compared and what was found. |

## 3. Terms

| Term | Meaning |
|---|---|
| **Reference** | The approved document. It is the source of truth. Shown on the left, in red. |
| **Current** | The document being checked. Shown on the right, in green. |
| **Finding** | One detected difference, such as a changed value, a missing row or a formatting change. |
| **Review item** | Content the tool could not fully compare. The reviewer must check it manually in the original files. |
| **Verify** | A finding that comes from OCR (reading text from an image). It must be confirmed against the image. |
| **Exact File Match** | Both files have identical SHA-256 hashes, so they are byte-for-byte the same file. |
| **Part** | A header, footer, footnote or endnote. |

---

## 4. Functional requirements

### FR-01 Selecting and validating documents

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-01.1 | The user selects one Reference and one Current file through a file picker that shows only `.docx` files. | 🟡 Partial (manual only) | TC-MAN-01 |
| FR-01.2 | **Compare** is enabled only when both files are selected and no comparison is running. | 🟡 Partial (manual only) | TC-MAN-01 |
| FR-01.3 | If a file cannot be read, the tool shows a clear message and does not compare. The cases are: no path (`DOCX-PATH-EMPTY`), not `.docx` (`DOCX-EXTENSION-INVALID`), not found (`DOCX-FILE-NOT-FOUND`), access denied (`DOCX-ACCESS-DENIED`), in use by another program (`DOCX-FILE-UNAVAILABLE`, "Close Microsoft Word… then click Compare again"), not a valid package (`DOCX-PACKAGE-INVALID`), and no document body (`DOCX-BODY-MISSING`). | 🟡 Partial (only "in use" is tested) | TC-IN-01 |
| FR-01.4 | Source documents are opened **read-only** and are never modified. | ✅ Met (by design: opened with `readOnly`) | (code review) |

### FR-02 Overall result

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-02.1 | If both files have the same SHA-256 hash, the result is **Exact File Match** with no findings. | ✅ Met | TC-RES-01 |
| FR-02.2 | If the files differ but no differences are found in the checked content, the result is **"No differences found in checked content"**. It must not say "identical". | ✅ Met | TC-RES-02 |
| FR-02.3 | Otherwise the result is **"N findings, M need review"**. | ✅ Met | TC-UI-01 |
| FR-02.4 | File metadata (author, dates, package IDs) is **not** compared. | ✅ Met | TC-RES-02 |
| FR-02.5 | If any review item exists, the result can never be "No differences found". | ✅ Met | TC-MIX-03 |

### FR-03 Content that is read

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-03.1 | **Body text**: all paragraphs, including spaces, tabs, line breaks, non-breaking hyphens and soft hyphens. | ✅ Met | TC-TXT-01…05 |
| FR-03.2 | **Headers and footers**: read per section and per type (default, first page, even pages). Each is labelled, for example "Header (first page), Section 2". | ✅ Met | TC-PRT-01, TC-PRT-04 |
| FR-03.3 | **Footnotes and endnotes**: numbered in the order they are referenced in the body ("Footnote 1"). | ✅ Met | TC-PRT-01 |
| FR-03.4 | **Tables** in the body and in parts, including merged cells (column span and row span), rows that start after skipped columns, and nested tables. A nested table's text is shown as lines of the outer cell. | ✅ Met | TC-RD-01, TC-RD-03, TC-TBL-06 |
| FR-03.5 | **Inline images** in the body, tables, headers, footers and notes. | ✅ Met | TC-RD-04, TC-IMG-07 |
| FR-03.6 | **Hidden text** (`w:vanish`) is detected. Text hidden or un-hidden in Current is reported ("Text hidden in Current"), and the viewer shows hidden text faded. | ✅ Met | TC-FMT-06, TC-SMP-01 |
| FR-03.7 | **Inserted symbols** (`w:sym`) are read as text. Symbol-font characters become their real characters (≤ ≥ ± µ °). Other symbol fonts keep a unique code, so a changed symbol is still found. | ✅ Met | TC-TXT-06, TC-SMP-01 |
| FR-03.8 | **Equations** (Office Math) are reported as a **review item** ("equation"), so they are never skipped silently. Comparing the equation content itself is not implemented. | 🟡 Partial | TC-RV-01 |
| FR-03.9 | **List numbering** (for example "5.2.1", "a)", bullets) must be compared. | ❌ Gap | TC-GAP-04 (known-gap test) |

### FR-04 Pairing content between the two documents

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-04.1 | A paragraph inserted or deleted in the middle must not make the following paragraphs look changed. | ✅ Met | TC-ALN-01 |
| FR-04.2 | A paragraph edited in place, even heavily, is compared word by word. It is not reported as "deleted + added". | ✅ Met | TC-ALN-02, TC-ALN-04 |
| FR-04.3 | Each paragraph is paired with its counterpart, so the viewer can line them up and show the match when unchanged content is clicked. | ✅ Met | TC-ALN-03 |
| FR-04.4 | Each part is compared only with the same part in the other document. Body text is never paired with a header. | ✅ Met | TC-PRT-04 |
| FR-04.5 | Tables are paired by content, so a table inserted in the middle does not shift later tables. | ✅ Met | TC-TBL-07 |
| FR-04.6 | Table rows are aligned by their first-column label when labels are unique. Columns are aligned by their header row when headers are unique. A removed middle row or column must not shift other values. | ✅ Met | TC-TBL-03, TC-TBL-04 |
| FR-04.7 | Images are paired by identical content. An image is reported as "moved" only when its paragraph is paired with a different paragraph, not just because content was added above it. | ✅ Met | TC-IMG-07 |
| FR-04.8 | Paragraphs **inside one table cell** must be paired by content, not by position. | 🐞 Defect (paired by position) | TC-GAP-14 (known-gap test) |

### FR-05 Text differences

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-05.1 | A changed value is reported as the **complete value** (for example `MD-4482` → `MD-4482-R`), without unrelated neighbouring words. A replacement of adjacent words ("Confidential" → "Internal Use") is **one** finding. | ✅ Met | TC-TXT-02 |
| FR-05.2 | Two changed values on one line are reported as **two separate findings**. | ✅ Met | TC-TXT-03 |
| FR-05.3 | Added, removed and changed spaces are reported, and spaces are shown visibly ("␠"). | ✅ Met | TC-TXT-05, TC-UI-01 |
| FR-05.4 | Text missing from Current is reported as "Not present" and marked with a caret at the exact position. | ✅ Met | TC-TXT-04 |

### FR-06 Formatting differences

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-06.1 | Direct character formatting is compared: font, size, bold, italic, underline, strikethrough, subscript, superscript, font colour, highlight and character spacing. | ✅ Met | TC-FMT-01, TC-FMT-02, TC-FMT-05 |
| FR-06.2 | Direct paragraph formatting is compared: style, alignment, left indent, first-line indent, space before, space after and page break before. | ✅ Met | TC-FMT-04 |
| FR-06.3 | Formatting is compared character by character. How Word splits text into runs must not create findings. | ✅ Met | TC-FMT-03 |
| FR-06.4 | One formatting change over a span produces **one** finding, even if Word stores the span as several runs. | 🐞 Defect (one finding per run segment) | TC-GAP-10 (known-gap test) |
| FR-06.5 | "Not set" and an explicit default (underline `none`, colour `auto`, highlight `none`, alignment `start`/`end`) are treated as equal. | ✅ Met | TC-FMT-07 |
| FR-06.6 | Character styles, inherited style formatting, caps, line spacing, right and hanging indent, cell shading, borders and widths are compared. | ❌ Gap | TC-GAP-05 (known-gap test) |

### FR-07 Table differences

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-07.1 | Changed cell values are reported with table, row and column, plus the row label and column header. | ✅ Met | TC-TBL-01, TC-UI-01 |
| FR-07.2 | A table present on one side only is reported. | ✅ Met | TC-TBL-02 |
| FR-07.3 | A changed row count is reported only when the added or missing rows are not already reported value by value, so one deleted row is not counted twice. A changed cell count within a row is reported. | ✅ Met | TC-TBL-08 |
| FR-07.4 | Missing or added rows and columns are reported value by value ("Row missing from Current", "Column missing from Current"). | ✅ Met | TC-TBL-03, TC-TBL-04 |
| FR-07.5 | Identical tables, including merged ones, produce no findings. | ✅ Met | TC-TBL-05 |

### FR-08 Image differences

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-08.1 | An image added, removed or with changed content is reported. | ✅ Met | TC-IMG-07 |
| FR-08.2 | The changed area inside an image is located by comparing pixels, and shown as an enlarged crop. | ✅ Met | TC-IMG-01, TC-UI-01 |
| FR-08.3 | Text inside the changed area is read with the computer's built-in OCR (Windows OCR on Windows, Apple Vision on macOS) and reported value by value ("Possible text change in image"). OCR results are always marked **verify**. | ✅ Met (Windows, macOS) | TC-IMG-01, TC-IMG-02, TC-IMG-06 |
| FR-08.4 | Chart values are matched to their category label, so a bar that moved is not reported against the wrong category. | ✅ Met | TC-IMG-03 |
| FR-08.5 | An OCR difference without a pixel change is **not** reported, because OCR can read identical pixels differently. | ✅ Met | TC-IMG-04 |
| FR-08.6 | If OCR is unavailable or fails, the visual change is still reported and **no value is invented**. | ✅ Met | TC-IMG-05 |
| FR-08.7 | Changes to displayed width and height, alt text, image title, alignment and placement are reported. | 🟡 Partial (not tested) | — |
| FR-08.8 | Changed pixels close together on one line of an image (one value such as 99.1 → 98.4) form **one** region and one finding. Separate lines stay separate. | ✅ Met | TC-IMG-08, TC-SMP-01 |

### FR-09 Image replaced by a table, or a table by an image

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-09.1 | When one side has exactly one image and no table, and the other side has no image and at least one table, under the **same unique heading**, they are compared as one "representation change". This works in both directions. | ✅ Met | TC-MIX-01, TC-MIX-06 |
| FR-09.2 | OCR values are mapped to table cells by row label and column position. Each differing cell is one finding with its image region and cell location. | ✅ Met | TC-MIX-01, TC-MIX-04 |
| FR-09.3 | Even when all recognized values match, the finding stays marked **verify** and "representation changed". | ✅ Met | TC-MIX-02 |
| FR-09.4 | If OCR fails, no value is reported, either matching or different. | ✅ Met | TC-MIX-03 |
| FR-09.5 | If rows cannot be aligned reliably, the tool says so and does not pair values. | ✅ Met | TC-MIX-05 |
| FR-09.6 | Other text in the same section that changed is reported as **changed**, not as deleted. | 🐞 Defect | TC-GAP-13 (known-gap test) |

### FR-10 Review items (content that cannot be compared)

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-10.1 | The following produce a **Needs review** item at their location: floating images, text boxes, equations, embedded objects and charts, legacy drawings, SmartArt, block-level content controls, tracked changes, custom XML, embedded documents, and table rows or cells inside content controls. | 🟡 Partial (only table and equation review items tested) | TC-ALN-02, TC-RV-01 |
| FR-10.2 | Every review item can be located in the viewer and reached with **Next to review**. | ✅ Met | TC-ALN-02, TC-UI-01 |
| FR-10.3 | Text inside an **inline** content control is read and compared like other text, and does **not** create a review item. | ✅ Met | TC-RD-05, TC-SMP-01 |

### FR-11 Page location

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-11.1 | When Word saved its page layout in the file, each finding shows its page number on each side. | ✅ Met | TC-PG-01, TC-PG-04, TC-PG-06 |
| FR-11.2 | With only manual page breaks, the page is shown as **"estimated"**. | ✅ Met | TC-PG-03 |
| FR-11.3 | With no page information, the location shows **"Page unavailable"** and never a false "Page 1". | ✅ Met | TC-PG-05 |
| FR-11.4 | A manual break and Word's saved page marker at the same place count as **one** page boundary. | ✅ Met | TC-PG-02, TC-PG-04 |
| FR-11.5 | In headers and footers, page-number and date fields are compared as fields ("[Page number]"), not as printed values. In the body, fields keep their printed value. | ✅ Met | TC-PRT-02, TC-PRT-03 |
| FR-11.6 | Locations are given as a reviewer finds them in Word. A header, footer or note is named ("Header, Paragraph 1"). Paragraphs are counted within their area **without table cells**. Table cells show table, row, column, row label and column header. Word's internal run numbers are never shown. The app and the PDF use the same wording. | ✅ Met | TC-LOC-01, TC-SMP-01, TC-RPT-02 |

### FR-12 Side-by-side viewer

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-12.1 | Reference (left) and Current (right) are shown **line-aligned**: matching content starts at the same height, and missing content leaves a gap of the same size on the other side. | ✅ Met | TC-UI-01 |
| FR-12.2 | Colours: **red** = Reference value, **green** = Current value, **pink** = missing content or formatting change, **amber** = needs review, **grey** = image/table representation change, **blue outline** = unchanged content that was clicked. | ✅ Met | TC-UI-01 |
| FR-12.3 | Clicking a highlight focuses the list on that change and shows its details. Clicking unchanged content shows "No difference" and the matching content on the other side. | ✅ Met | TC-UI-01 |
| FR-12.4 | **Hide highlights** hides the highlights for easier reading, and changes remain clickable. | ✅ Met | TC-UI-01 |
| FR-12.5 | Both panes scroll together. Zoom goes from 70% to 180% in 10% steps. | 🟡 Partial (manual only) | TC-MAN-02 |
| FR-12.6 | A strip beside each scroll bar shows a mark for every finding. Clicking a mark selects that finding. |
| FR-12.7 | The screen is easy to follow without training: ① and ② boxes to choose the files (file names shown, full path on hover); a plain-language result ("21 differences found · 1 to check yourself"); a colour key; "3 of 21" next to Previous / Next; guidance text on the empty pages and in the empty explanation panel; pages fit their pane (no sideways scrolling, words wrap whole) from the minimum window size 1100 × 680 up. Screenshots: [docs/screenshots](screenshots/). | ✅ Met | TC-UI-01, TC-MAN-08 | 🟡 Partial (manual only) | TC-MAN-02 |

### FR-13 Findings list and details panel

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-13.1 | Each item in the list shows the change type, location, Reference value and Current value. | ✅ Met | TC-UI-01 |
| FR-13.2 | The details panel shows what changed, Reference (required) and Current (found) values, where, a recommended action, page notes, the review reason, and image crops. | ✅ Met | TC-UI-01 |
| FR-13.3 | Filters: All differences, Missing or added content, Table values and structure, Images and recognized text, Check yourself. | 🟡 Partial (manual only) | TC-MAN-03 |
| FR-13.4 | **Previous / Next** move through the **visible (filtered)** list. | 🐞 Defect (they ignore the filter and reset the list) | TC-GAP-07 |
| FR-13.5 | The list and Previous / Next follow **document reading order**. | 🐞 Defect (grouped by type) | TC-GAP-08 (known-gap test) |
| FR-13.6 | After a manual compare, the first real change in Current is selected automatically. | 🟡 Partial (manual only) | TC-MAN-01 |

### FR-14 Automatic refresh

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-14.1 | When either file is saved, the comparison re-runs automatically about 1 second after the last save. The reviewer's position is kept. | 🟡 Partial (manual only) | TC-MAN-04 |
| FR-14.2 | If a file is still being written, the tool waits and retries instead of failing. | 🟡 Partial (manual only) | TC-MAN-04 |
| FR-14.3 | If the user selects a different file while a comparison runs, the status message must not stay on "Comparing…". | 🐞 Defect | TC-GAP-09 |

### FR-15 PDF report

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-15.1 | A PDF is created **only** when the user clicks **Save PDF report**. | ✅ Met | TC-RPT-01 |
| FR-15.2 | The report contains: file names, overall result, number of findings and how many need verification, the comparison scope, a summary by change type, and one entry per finding (ID, type, locations, Reference and Current values, context, review note). | ✅ Met | TC-RPT-01, TC-RPT-02, TC-MAN-05 |
| FR-15.3 | Whitespace values are shown visibly in the report ("[space]", "[tab]"), and absent content reads "Not present". | ✅ Met | TC-RPT-02, TC-RPT-03 |
| FR-15.4 | The report uses the **same change names** as the app (for example "Row missing from Current", not "Deleted paragraph"). | ✅ Met | TC-RPT-02 |

### FR-16 Exact pages (the real layout)

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-16.1 | A switch **Reading view / Exact pages** shows both documents as their **real pages**, laid out by **Microsoft Word** (Windows, exactly as Word prints) or **LibreOffice** (any system, nearly identical), side by side. The engine used is named above the pages. | ✅ Met (LibreOffice tested on macOS; Word path untested on Windows) | TC-EXV-03, TC-MAN-09 |
| FR-16.2 | The differences are marked **on the real pages** in the same colours as Reading view: values, formatting, whole paragraphs, tables, header/footer/footnote text, and the changed area inside an image. Content missing on one side is a pink line where it belongs. Clicking a mark selects the difference; selecting a difference scrolls both documents to it. | ✅ Met | TC-EXV-02, TC-EXV-03 |
| FR-16.3 | When the selected difference has no place on a document's pages (for example text hidden in Current), the pane says so instead of marking a wrong place. | ✅ Met | TC-EXV-02, TC-MAN-09 |
| FR-16.4 | Zoom enlarges the real pages beyond the pane width (the pane then scrolls sideways), so small print can be read. Pages are drawn at 144 dpi so they stay sharp. | ✅ Met | TC-MAN-09 |
| FR-16.6 | **Show on the real pages** (in the explanation panel) opens a window with the approved page and the Current page side by side, only the selected difference outlined, both zoomed to it at the same size and centred on it. It has ‹ Previous / Next › (also ← →) which keep the main window's selection in step, − / +, Whole page, Zoom to change, and Close (Esc). A side without a place for the difference says why (not in this document, hidden, or a pink line where the content would be) and shows the same area of its page. | ✅ Met | TC-UI-02, TC-MAN-11 |
| FR-16.5 | The layout engine only ever works on a **temporary copy**; laid-out pages are reused while a file is unchanged and are deleted when the app closes. If no engine is installed, the pane explains how to get one. | ✅ Met | TC-EXV-03, TC-MAN-09 |

### FR-17 Open the original

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-17.1 | **Open in Word** above each document opens a **read-only copy** of it in Microsoft Word (or the default app for .docx when Word is not installed; the button then says "Open original"), so the approved file cannot be changed by accident. | 🟡 Partial (manual only) | TC-MAN-10 |

### FR-18 Comparison history

| ID | Requirement | Status | Verified by |
|---|---|---|---|
| FR-18.1 | Every completed comparison is saved automatically: date and time, reviewer (computer user name) and computer, both files (name, full path, SHA-256 fingerprint), the result, the number of differences and items to check, the count per type of change, and every difference as found (type, both values, both locations). Comparing the same two unchanged files again does not add a duplicate. Saving never interrupts a comparison. | ✅ Met | TC-HIS-01, TC-HIS-02 |
| FR-18.2 | **History** (next to Compare) lists the saved comparisons, newest first, with a search by file name, result, reviewer or date. The selected comparison shows its details and, for each file, whether it is **unchanged, changed or missing** since that comparison (by fingerprint). | ✅ Met | TC-HIS-04, TC-MAN-12 |
| FR-18.3 | From History: **Compare these files again**, **Export this comparison (CSV)**, **Export all (CSV)**, **Delete from history**, and **Clear history** (asks for confirmation). CSV files open correctly in Excel (UTF-8), and values that Excel would run as formulas are written as text. | ✅ Met | TC-HIS-03, TC-HIS-04, TC-MAN-12 |
| FR-18.4 | The history is one file per user on this computer (`history.jsonl` in the user's application-data folder; on macOS `~/Library/Application Support/DocumentChecker/`). A damaged line is skipped without losing the other entries. | ✅ Met | TC-HIS-02 |

---

## 5. Non-functional requirements

| ID | Requirement | Status |
|---|---|---|
| NFR-01 | **Local only.** No document content is sent over the network. OCR uses the recognition engine built into the computer (Windows OCR or Apple Vision), on the same machine. | ✅ Met (no network code) |
| NFR-02 | **Read-only.** Source files are never modified. | ✅ Met |
| NFR-03 | **Honest claims.** Only Exact File Match claims identity. OCR findings are always "verify". Failed or ambiguous OCR never certifies a match. | ✅ Met |
| NFR-04 | **Platform.** Runs on .NET 10 with Avalonia 12 on Windows, macOS and Linux. OCR uses **Windows OCR** on Windows (needs Windows PowerShell and an installed OCR language) and **Apple Vision** on macOS 10.15 or later (built in, run through `osascript`). Linux has no OCR: image changes are still reported as visual changes with crops, and no values are invented. Auto-refresh after saving works on Windows and macOS. | ✅ Met (Windows, macOS); 🟡 Linux without OCR |
| NFR-05 | **OCR limits.** Each image read times out after 30 seconds. Results are cached for the session. | ✅ Met |
| NFR-06 | **Confidentiality.** `.docx` files are never committed to the repository (`.gitignore`), except the synthetic `samples/`. Exact pages and Open in Word make temporary copies of the documents in the user's temporary folder (a folder per session); they are deleted when the app closes. The **comparison history** contains file names, paths and the differing values; it stays in the user's own application-data folder on this computer and can be cleared from the History window. | ✅ Met |
| NFR-07 | **Performance.** No target is defined yet. Known risk: every click rebuilds both document views, which may be slow for documents of 200+ pages. | ❌ Gap (target needed) |
| NFR-09 | **Dependencies.** Third-party packages have no known security vulnerabilities. Exact pages use PDFium (bblanchon.PDFium 156.0.8066, Apache-2.0) and an installed Word or LibreOffice. SixLabors.ImageSharp 2.1.13 currently has 3 high and 2 moderate advisories (it decodes every image in the documents). Note that ImageSharp 3.x needs a commercial licence for large companies. | ❌ Gap (upgrade needed) |
| NFR-08 | **Report fonts.** The PDF uses a locally installed font (Arial, Segoe UI or DejaVu Sans). If none is found, report creation fails with a message. | ✅ Met |

---

## 6. Out of scope (current version)

The following are **not** verified by this version. Where they are detected, they produce a review item:

- Exact Word pagination and page layout, including floating image position and text wrapping.
- Formatting inherited from styles (only directly applied formatting is compared).
- Tracked revisions, comments, text boxes, embedded objects, charts, SmartArt, the content of equations, and the contents of block-level content controls.
- Semantic equivalence (for example "30 days" vs "thirty days").
- File types other than `.docx` (`.doc`, `.pdf`, `.rtf`).

---

## 7. Open questions for stakeholders

These need a decision before the requirements can be considered final:

1. **Document types.** Which documents will be checked (SOPs, specifications, labels, batch records)? This decides how urgent the gaps in FR-03.6 to FR-03.9 are.
2. **Deployment.** Will reviewers use Windows PCs, Macs, or both? On Windows, OCR needs PowerShell to be allowed. On macOS, OCR needs `osascript` (AppleScript/JavaScript) not to be blocked by device management. If either is restricted, OCR will not work, and image changes fall back to visual changes.
3. **Performance target.** What is the largest document (pages, tables, images) that must open, and how fast?
4. **Validation.** Does the tool need formal computerized-system validation (for example GAMP 5 / 21 CFR Part 11)? If yes, this document and [TEST_CASES.md](TEST_CASES.md) become the URS and test protocol and need sign-off.
5. **Report.** Must the PDF include the image crops, reviewer name, date and time, or a signature?
6. **History as a record.** Is the local history enough, or must comparisons be kept in a shared, tamper-evident place (for example for audits under 21 CFR Part 11)? The local file can be cleared by the user and is not signed.

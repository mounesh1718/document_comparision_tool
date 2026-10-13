# Document Checker: Test Cases

| | |
|---|---|
| **Version described** | Commit `bf00bbf` plus the fixes of 2026-10-12 |
| **Requirements** | [REQUIREMENTS.md](REQUIREMENTS.md). The **Req** column links each test case to a requirement ID. |
| **Automated tests** | `tests/DocumentChecker.Engine.Tests` (xUnit): 83 test methods, 107 test cases including data variations. Each test is tagged with its test-case ID, for example `[Trait("TC", "TC-TBL-03")]`. |

## How to run

```
dotnet test tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj
```

Requires the .NET 10 SDK. Tests never open Microsoft Word (the Exact pages tests and the UI test set `DOCHECK_LAYOUT_ENGINE=libreoffice`, which switches Word off for the run) and never write to the user's real comparison history (each history test uses its own file; the UI test points `DOCHECK_HISTORY_FILE` and `DOCHECK_SETTINGS_FILE` at temporary files). Most tests build small Word files in a temporary folder, run the comparison, check the result and delete the files. TC-SMP-01 and TC-RPT-02 use the synthetic documents in `samples/`. No real customer documents are used.

**Expected result today on Windows and macOS with LibreOffice installed:** 101 passed, 0 failed, 6 skipped (the 6 known-gap tests). Without LibreOffice, TC-EXV-03 is also skipped, and the UI test leaves out its TC-EXV-05 part. On Linux the 3 OCR tests are also skipped, because Linux has no built-in text recognition.

Run the tests of one test case:

```
dotnet test tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj --filter "TC=TC-TBL-03"
```

Run the known-gap tests to see which gaps are still open (each fails until its gap is fixed):

```
DOCHECK_RUN_KNOWN_GAPS=1 dotnet test tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj --filter "FullyQualifiedName~KnownGapTests"
```

## Status key

| Status | Meaning |
|---|---|
| **Auto** | Automated. Runs on every operating system. |
| **Auto (OCR)** | Automated. It uses the computer's own text recognition: Windows OCR on Windows, Apple Vision on macOS. On Linux it is reported as **Skipped**, never as passed. |
| **Auto (LibreOffice)** | Automated. It lays documents out with LibreOffice; where LibreOffice is not installed it is reported as **Skipped**. |
| **Known gap** | An automated test of the correct behaviour for a known gap. It fails today, so it is reported as **Skipped** ("Known gap TC-GAP-xx"). Run it with `DOCHECK_RUN_KNOWN_GAPS=1`. When it passes, the gap is fixed: change its attribute to `[Fact]`. |
| **Manual** | Not automated. A person follows the steps. |
| **Proposed** | A test for a known gap or defect. It is not written yet and is **expected to fail** until the code is fixed. |

## Summary

| Area | Test cases | Auto | Auto (OCR) | Known gap | Manual | Proposed |
|---|---|---|---|---|---|---|
| Input and overall result | IN, RES | 3 | – | – | – | – |
| Text | TXT | 6 | – | – | – | – |
| Formatting | FMT | 7 | – | – | – | – |
| Reading the document | RD | 5 | – | – | – | – |
| Tables | TBL | 8 | – | – | – | – |
| Images | IMG | 6 | 2 | – | – | – |
| Image ↔ table | MIX | 6 | 1 | – | – | – |
| Headers, footers, notes | PRT | 4 | – | – | – | – |
| Page numbers | PG | 6 | – | – | – | – |
| Pairing | ALN | 4 | – | – | – | – |
| Review items and locations | RV, LOC | 2 | – | – | – | – |
| Sample documents | SMP | 1 | – | – | – | – |
| Viewer (UI) | UI | 2 (one test method) | – | – | – | – |
| PDF report | RPT | 3 | – | – | – | – |
| Exact pages | EXV | 3 (+2 with LibreOffice) | – | – | – | – |
| Comparison history | HIS | 4 (5 test methods) | – | – | – | – |
| Choosing what to compare | OPT | 4 | – | – | – | – |
| Manual checks | MAN | – | – | – | 13 | – |
| Known gaps and defects | GAP (15) | 4 fixed, now covered above | – | 6 | – | 5 |

---

## Which test cases compare two Word documents

**51 test methods compare two Word documents end to end.** Each builds a Reference and a Current `.docx`, runs the real comparison, and checks the findings. The documents are built in a temporary folder by the test, except TC-SMP-01 and TC-RPT-02, which use the kept pair in `samples/`.

| Kind | Test cases |
|---|---|
| **Compare two documents** (51 methods) | TC-EXV-03 · TC-RES-01, TC-RES-02 · TC-TXT-01, 04, 05, 06 · TC-FMT-01, 03, 04, 05, 06, 07 · TC-RD-05 · TC-TBL-01…08 · TC-IMG-01, 07, 08 · TC-MIX-01…07 · TC-PRT-01…04 · TC-PG-01 · TC-ALN-01…04 · TC-RV-01 · TC-SMP-01 (2 methods) · TC-RPT-02 · known gaps TC-GAP-04, 05, 08, 10, 13, 14 |
| **Read one document** (no comparison) | TC-IN-01 · TC-RD-01…04 · TC-PG-02…06 · TC-LOC-01 |
| **Test one piece of logic directly** | TC-EXV-01 (reading PDF pages) · TC-EXV-02 (finding differences on pages) · TC-TXT-02, TC-TXT-03 (text differences) · TC-TXT-06 symbol mapping · TC-IMG-02…05 (image text with given OCR words) · TC-IMG-06 (the OCR engine itself) · TC-RPT-03 (printed values) |
| **Screen and report** | TC-UI-01 (the viewer, which also runs a two-document comparison internally) · TC-RPT-01 (PDF created) |

## 1. Input and overall result

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-IN-01 | FR-01.3 | A document is open in another program | File locked exclusively | (any) | Comparison is not run. Message code `DOCX-FILE-UNAVAILABLE`, naming the file and saying "click Compare again". | `DocumentComparisonServiceTests.Reader_reports_a_clear_message_when_a_document_is_locked` | Auto |
| TC-RES-01 | FR-02.1 | The same file is selected on both sides | `same.docx` | `same.docx` | **Exact File Match** with zero findings. | `DocumentComparisonServiceTests.Compare_returns_exact_file_match_for_the_same_file` | Auto |
| TC-RES-02 | FR-02.2, FR-02.4 | Two separately saved files with identical content | "Same visible content" | "Same visible content" | **No differences found in checked content**. The file hashes are different, and there are zero findings. | `DocumentComparisonServiceTests.Compare_ignores_package_and_metadata_differences_when_visible_content_matches` | Auto |

## 2. Text

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-TXT-01 | FR-05.1, FR-06.1 | A number changes, and a subscript is removed | "Payment within **30** days" / "CO₂" | "Payment within **45** days" / "CO2" | One text finding `30` → `45` with positions. One formatting finding "Subscript On → Off". | `DocumentComparisonServiceTests.Compare_finds_paired_text_and_subscript_formatting_differences` | Auto |
| TC-TXT-02 | FR-05.1 | The changed part is reported as the complete value (9 variations, see below) | see table | see table | Exactly one finding containing only the changed value. | `ContentDifferenceTests.Text_changes_show_complete_values_without_including_unrelated_text` | Auto |
| TC-TXT-03 | FR-05.2 | Two values change on one line | "Quantity **10** price **1,250**" | "Quantity **15** price **1,750**" | Two separate findings: `10` → `15` and `1,250` → `1,750`. | `ContentDifferenceTests.Two_changed_values_on_one_line_remain_separate` | Auto |
| TC-TXT-04 | FR-05.4 | A line is deleted in Current | "Required line" | (empty paragraph) | Finding: "Required line" → "(no corresponding content)", with a zero-length position in Current for the caret. | `DocumentComparisonServiceTests.Compare_detects_a_line_missing_from_the_current_document` | Auto |
| TC-TXT-05 | FR-05.3 | An extra space is removed | "Payment due in␠␠30 days" | "Payment due in␠30 days" | One finding: Reference " " → Current "(no corresponding content)". | `DocumentComparisonServiceTests.Compare_detects_extra_or_missing_whitespace` | Auto |
| TC-TXT-06 | FR-03.7 | A symbol inserted from the Symbol font changes, plus the mapping of 10 symbol codes | "Total impurities **≤** 1.0%" (Symbol `F0A3`) | "Total impurities **≥** 1.0%" (Symbol `F0B3`) | The text reads "Total impurities ≤ 1.0%". One finding `≤` → `≥`. Codes map correctly (space, μ, ∼, €, °, ±, √, ⎭, ≥), and a Wingdings code keeps its own unique character. | `ContentCoverageTests.Symbol_font_characters_are_read_and_compared_as_text`, `ContentCoverageTests.Symbol_codes_map_to_their_characters` (10 cases) | Auto |

**TC-TXT-02 data variations**

| # | Reference text | Current text | Reported Reference value | Reported Current value |
|---|---|---|---|---|
| a | Qty 10 | Qty 15 | `10` | `15` |
| b | SKU MD-4482 | SKU MD-4482-R | `MD-4482` | `MD-4482-R` |
| c | abc | Xabc | `abc` | `Xabc` |
| d | Payment in 30 days | Payment in 45 days | `30` | `45` |
| e | Qty 10␠␠units | Qty 10␠units | `␠` | (nothing) |
| f | abc | abc def | (nothing) | `␠def` |
| g | abc def | abc | `␠def` | (nothing) |
| h | alpha,beta | alpha beta | `,` | `␠` |
| i | controlled release | controlled-release | `␠` | `-` |
| j | ACME Confidential | ACME Internal Use | `Confidential` | `Internal Use` (one finding, not two) |

## 3. Formatting

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-FMT-01 | FR-06.1 | A subscript is lost when runs are merged, in body text and in a table | "Water: H₂O" (3 runs) in the body and in a table cell | "Water: H2O" (1 run) in both places | Two "Subscript On → Off" findings, both at character 9 (the "2"). One of them is located in the table cell. | `DocumentComparisonServiceTests.Merged_runs_do_not_hide_formula_subscripts_in_body_or_tables` | Auto |
| TC-FMT-02 | FR-06.1 | A subscript is removed (CO₂ → CO2) | "CO₂" | "CO2" | "Subscript": Reference **On**, Current **Off**. | Same test as TC-TXT-01 | Auto |
| TC-FMT-03 | FR-06.3 | Same formatting but different run splitting; then a superscript is lost after an insertion | "AB²" (2 runs) | **(a)** "XAB²" (4 runs). **(b)** "XAB2" (1 run, no superscript). | **(a)** No formatting finding. **(b)** One "Superscript" finding at Reference position 2 and Current position 3, so the formatting follows the moved text. | `DocumentComparisonServiceTests.Formatting_follows_text_after_insertions_and_ignores_equivalent_run_splitting` | Auto |
| TC-FMT-04 | FR-06.2 | Paragraph alignment and spacing change | "Approved title": centred, space after 120 | Same text: right-aligned, space after 240 | "Paragraph alignment" center → right. "Space after" 120 twips → 240 twips. | `DocumentComparisonServiceTests.Compare_detects_paragraph_alignment_and_spacing_changes` | Auto |
| TC-FMT-05 | FR-06.1 | Underline and highlight change | "Value": single underline, yellow highlight | "Value": double underline, green highlight | "Underline" single → double. Highlight yellow → green. | `MixedComparisonTests.Underline_and_highlight_values_remain_distinct_and_readable` | Auto |
| TC-FMT-06 | FR-03.6 | Text is hidden in Current | "Keep out of reach of children." (visible) | Same text with **Hidden** formatting | One finding: "Hidden" Off → On, named "Text hidden in Current". | `ContentCoverageTests.Text_hidden_in_current_is_reported` | Auto |
| TC-FMT-07 | FR-06.5 | Explicit defaults vs not set | Plain "Same text" | Same text with colour `auto`, highlight `none`, underline `none`, alignment `start` | **No** findings. Result "No differences found". | `ContentCoverageTests.Explicit_default_formatting_is_not_reported_as_a_change` | Auto |

## 4. Reading the document

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-RD-01 | FR-03.4 | Text in a table cell is read | A table with one cell "Cell value 15" | Read succeeds. One paragraph with text "Cell value 15". | `DocumentComparisonServiceTests.Reader_extracts_table_cell_paragraphs_for_comparison` | Auto |
| TC-RD-02 | FR-03.4, FR-06.2 | Headings, layout and block order are kept | A "Heading1" paragraph (centred, spacing 120/60) followed by a table | Block 1 is the paragraph with style "Heading1" and alignment "center". Block 2 is the table, with "Table value" in its first cell. | `DocumentComparisonServiceTests.Reader_preserves_document_display_blocks_and_paragraph_layout` | Auto |
| TC-RD-03 | FR-03.4 | Merged cells are read correctly | "Strength" merged across 2 rows; "Composition" merged across 2 columns | Each cell has the correct grid column, column span, row span and "merged from above" flag. There are no review items. | `NoiseReductionTests.Merged_cells_are_read_with_their_grid_position_and_span` | Auto |
| TC-RD-04 | FR-03.5 | An image in the header is read | A header containing a logo image | Exactly one image is read. It belongs to the **Header** and contains image data. | `HeaderFooterTests.Header_images_are_read_from_the_header_part` | Auto |
| TC-RD-05 | FR-10.3 | A value inside an inline content control changes | "Batch size: [control] 100 tablets" vs "… 200 tablets" | Exactly one finding `100` → `200`, and **no** review item. | `ContentCoverageTests.Text_in_an_inline_content_control_is_compared_without_a_review_item` | Auto |

## 5. Tables

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-TBL-01 | FR-07.1 | A cell value changes | Cell "10" | Cell "15" | One finding `10` → `15`, located at Table 1, Row 1, Cell 1. | `DocumentComparisonServiceTests.Compare_detects_table_cell_data_and_reports_table_cell_location` | Auto |
| TC-TBL-02 | FR-07.2 | A table exists only in Reference | Table "Table data" | A paragraph, no table | One table finding with property "Table", Current = "(no corresponding table)". | `DocumentComparisonServiceTests.Compare_reports_when_a_table_exists_on_only_one_side` | Auto |
| TC-TBL-03 | FR-04.6, FR-07.4 | A middle row is deleted | Item/Qty: Sensor 10, **Cable 20**, Motor 30 | Item/Qty: Sensor 10, Motor 30 | "Missing row value" for `Cable` and `20`. **No** finding for Motor or 30, and no text findings. | `MixedComparisonTests.Missing_middle_row_does_not_shift_subsequent_values` | Auto |
| TC-TBL-04 | FR-04.6, FR-07.4 | A middle column is deleted | Item / **Qty** / Price: Sensor / 10 / 120 | Item / Price: Sensor / 120 | "Missing column value" for `Qty` and `10`. No text findings. | `MixedComparisonTests.Missing_middle_column_does_not_shift_remaining_columns` | Auto |
| TC-TBL-05 | FR-07.5 | Merged tables: identical, then one value changed | Merged table with "250 mg" | **(a)** Same table. **(b)** Same table with "500 mg". | **(a)** Zero findings. **(b)** Exactly one finding `250` → `500`. | `NoiseReductionTests.Identical_merged_tables_produce_no_findings_and_a_changed_value_is_found` | Auto |
| TC-TBL-06 | FR-03.4 | A value inside a nested table changes | Outer cell holding a nested table with "Inner 30" | Same, with "Inner 45" | Exactly one finding `30` → `45`, located in a table cell, with no review warning. | `NoiseReductionTests.Text_inside_a_nested_table_is_compared_without_a_review_warning` | Auto |
| TC-TBL-07 | FR-04.5 | A table is inserted in the middle | Tables: Assay, Dissolution, Water | Tables: Assay, **Impurities**, Dissolution, Water | Exactly one finding: "Impurities" table added. Dissolution and Water still pair with their own tables. | `NoiseReductionTests.A_table_inserted_in_the_middle_does_not_shift_the_tables_after_it` | Auto |
| TC-TBL-08 | FR-07.3 | A row is deleted; the row count must not be reported a second time | Test/Limit: Assay, **Water**, Impurities | Test/Limit: Assay, Impurities | Exactly two findings, "Water" and "NMT 2.0%", both "Row missing from Current". **No** "Rows" finding. | `ContentCoverageTests.A_deleted_row_is_reported_once_per_value_without_a_separate_row_count` | Auto |

## 6. Images

TC-IMG-02 to TC-IMG-05 use **pre-defined OCR results**, so they run on any operating system.

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-IMG-01 | FR-08.2, FR-08.3 | Text inside an image changes (real OCR) | Image "Payment terms: **30** days" | Image "Payment terms: **45** days" | One OCR finding `30` → `45`, with image regions on both sides. No technical "hash" or "relationship" findings are shown. | `DocumentComparisonServiceTests.Compare_reads_changed_words_from_embedded_images_in_docx` | Auto (OCR) |
| TC-IMG-02 | FR-08.3 | Several values change in one row of an image table | OCR: "Sensor 2 1,250" | OCR: "Sensor 3 1,750" | Two separate findings, `2` → `3` and `1,250` → `1,750`, each with its own region and the row context "Sensor". | `ContentDifferenceTests.Image_table_values_are_individually_selectable_and_have_row_context` | Auto |
| TC-IMG-03 | FR-08.4 | Bar chart values change and the bars move | Jan 70, Feb 81, Mar 88 | Jan 70, Feb 79, Mar 86 (labels at new heights) | Two findings: "Chart category: Feb" 81 → 79 and "Chart category: Mar" 88 → 86. Jan is not reported. | `ContentDifferenceTests.Chart_values_follow_the_category_when_bar_heights_change` | Auto |
| TC-IMG-04 | FR-08.5 | OCR reads the same pixels differently | OCR reads "10" | OCR reads "IO", with no changed pixels | **No** finding. | `ContentDifferenceTests.Recognition_differences_without_pixel_changes_are_not_reported` | Auto |
| TC-IMG-05 | FR-08.6 | OCR is unavailable but pixels changed | OCR error "OCR unavailable" | No words; one changed region | One **visual** change (not a text change) with the OCR error as its review note and the correct region. No values are invented. | `ContentDifferenceTests.Unreadable_image_content_keeps_a_visual_change_without_inventing_values` | Auto |
| TC-IMG-06 | FR-08.3 | The computer's OCR (Windows OCR or Apple Vision) reads real text with correct positions | Image "Payment terms: 30 days" | Image "Payment terms: 45 days" | OCR returns "30" and "45" at the correct place in the original image size, and the comparer reports `30` → `45`. | `ContentDifferenceTests.System_recognition_reads_actual_image_values_and_returns_original_pixel_coordinates` | Auto (OCR) |
| TC-IMG-07 | FR-04.7, FR-08.1 | New paragraphs and a new image are added above an existing logo | "Introduction", Logo | "Introduction", 2 new paragraphs, **Chart**, Logo | The logo is **not** reported as moved. Exactly one image finding: the chart was added, at paragraph 4. | `NoiseReductionTests.Images_below_new_content_are_not_reported_as_moved_and_a_new_image_is_reported_as_added` | Auto |
| TC-IMG-08 | FR-08.8 | One value changes inside an image, without OCR | Image "Assay **99.1**" | Image "Assay **98.4**" | Exactly **one** "Visual image change" finding with an image region, although two separate digits changed. | `ContentCoverageTests.One_changed_value_inside_an_image_is_one_visual_finding` | Auto |

## 7. Image replaced by a table

Every case has a "Heading1" heading and an image or a table under it. TC-MIX-01 to TC-MIX-06 use pre-defined OCR results.

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-MIX-01 | FR-09.1, FR-09.2 | An image is replaced by a table with a different value. Run twice: image in Reference, then image in Current. | Image "Quantity 30" (or the table) | Table "Quantity / 45" (or the image) | One OCR finding with the values the right way round, the image region of "30", and the table cell location (column 2). Marked image on one side and table on the other, with a review note. No generic "Image content" or "Table" findings. | `MixedComparisonTests.Image_and_table_values_share_one_change_with_real_coordinates_and_cell_location` (2 cases) | Auto |
| TC-MIX-02 | FR-09.3 | The image and the table hold the same values | Image "Quantity 30" | Table "Quantity / 30" | The result is still "Different", with exactly one finding (representation changed) saying "Recognized words match" and a review note. | `MixedComparisonTests.Matching_recognized_data_is_still_marked_for_review_and_representation_change` | Auto |
| TC-MIX-03 | FR-09.4, FR-02.5 | OCR fails | Image "Quantity 30" | Table "Quantity / 45" | No OCR value findings. A finding carries the note "OCR unavailable". The result is **not** "No differences found". | `MixedComparisonTests.Failed_OCR_never_invents_missing_or_matching_values` | Auto |
| TC-MIX-04 | FR-09.2 | OCR reads the image column by column | Image: Sensor 10 / Motor 30 | Table: Sensor 15 / Motor 30 | One finding `10` → `15` in row 1, with the image region of "10". Motor is not reported. | `MixedComparisonTests.Column_major_OCR_is_mapped_by_row_label_and_spatial_columns` | Auto |
| TC-MIX-05 | FR-09.5 | The image cannot be mapped to the table rows | Image: "Unreadable" | Table: Sensor 15 / Motor 30 | No value findings. Review note "Could not reliably align…". | `MixedComparisonTests.Ambiguous_image_grid_requires_review_instead_of_pairing_unrelated_values` | Auto |
| TC-MIX-06 | FR-09.1 | The image and the table are under different headings | Heading "Inventory" with an image | Heading "Payments" with a table | They are **not** paired: no "MIX-" findings. | `MixedComparisonTests.Different_named_sections_are_not_paired` | Auto |
| TC-MIX-07 | FR-09.2 | Real OCR, end to end | Image "Quantity 30" | Table "Quantity / 45" | An OCR finding `30` → `45` with a position in the table. | `MixedComparisonTests.Actual_OCR_compares_an_image_value_to_an_editable_table` | Auto (OCR) |

## 8. Headers, footers and notes

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-PRT-01 | FR-03.2, FR-03.3 | Header, footer and footnote text change | Header "ACME Ltd — **Confidential**", footer "Rev **A**", footnote "Source: **2024** audit." | "…**Internal**", "Rev **B**", "**2025**" | Three findings, labelled **Header**, **Footer** and **Footnote 1**. Unchanged footer text and body text are not reported. No review items. | `HeaderFooterTests.Header_footer_and_note_changes_are_found_and_located` | Auto |
| TC-PRT-02 | FR-11.5 | Footer page numbers print differently | Footer "Page 1 of …" (PAGE field) | Footer "Page 7 of …" | "No differences found". The footer text is read as "Page [Page number] of [Page count]". | `HeaderFooterTests.Page_number_fields_are_compared_as_fields_not_printed_values` | Auto |
| TC-PRT-03 | FR-11.5 | A date field in the body changes | "Effective: 01/01/2024" (DATE field) | "Effective: 02/02/2025" | One finding `01/01/2024` → `02/02/2025`. | `HeaderFooterTests.Fields_in_the_body_keep_their_printed_value` | Auto |
| TC-PRT-04 | FR-03.2, FR-04.4 | The footer is removed in Current | Footer with 2 lines | No footer | Two "paragraph removed" findings, both in the Footer. They are never paired with body or header text. | `HeaderFooterTests.A_footer_present_only_in_the_reference_is_reported_as_missing` | Auto |

## 9. Page numbers

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-PG-01 | FR-11.1 | A change is on different pages in each document | Reference: "Qty 10" after 2 saved page markers. Current: "Qty 15" after 1. | Reference location "Page 3 (saved layout)", Current location "Page 2". | `PageTrackingTests.Changed_text_uses_its_position_within_a_paragraph_and_separate_pages_on_each_side` | Auto |
| TC-PG-02 | FR-11.4 | A manual page break plus Word's saved marker, before a table | A page break, then a table cell starting with a saved marker | The table cell is on page **2**, not 3. | `PageTrackingTests.Explicit_break_and_cached_marker_are_not_counted_twice_in_a_table` | Auto |
| TC-PG-03 | FR-11.2 | Only manual page breaks, no saved layout | "First [page break] Second" | "First" is on page 1. "Second" is on page 2, marked **estimated**. | `PageTrackingTests.Manual_breaks_without_saved_layout_are_explicitly_estimated` | Auto |
| TC-PG-04 | FR-11.1, FR-11.4 | "Page break before" plus a saved marker | Paragraph 2 has "page break before" and a saved marker | Paragraph 2 is on page **2**, counted once. | `PageTrackingTests.Page_break_before_and_its_saved_marker_share_one_page_boundary` | Auto |
| TC-PG-05 | FR-11.3 | No page information in the file | One plain paragraph | The page is unknown and the location shows "Page unavailable", not "Page 1". | `PageTrackingTests.Documents_without_page_positions_do_not_claim_an_exact_page_one` | Auto |
| TC-PG-06 | FR-11.1 | An image placed after a saved page marker | Text, then a page marker followed by an image | The image is on page **2**. | `PageTrackingTests.Embedded_image_after_an_intrarun_break_gets_its_own_page` | Auto |

## 10. Pairing (alignment)

| ID | Req | Scenario | Reference | Current | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|---|
| TC-ALN-01 | FR-04.1 | A paragraph is inserted in the middle | A, B, C | A, **NEW**, B, C | Exactly **one** finding: paragraph "NEW" added. B and C are not reported. | `DocumentComparisonServiceTests.Compare_aligns_an_inserted_paragraph_without_marking_later_paragraphs_as_changed` | Auto |
| TC-ALN-02 | FR-04.2, FR-10.1, FR-10.2 | A mixed sample document (edits, additions, deletions, a table needing review, header and page fields) | Sample A | Sample B | Edited paragraphs are paired by similarity, not by position. One added paragraph. The removed word "extra " is found. Two removed paragraphs, each with a place in the other document. The table review items have locations. Header "Confidential" → "Internal". Page fields are not reported. | `ReviewMarkerTests.Edited_paragraphs_pair_by_similarity_and_review_items_have_locations` | Auto |
| TC-ALN-03 | FR-04.3 | A paragraph is inserted at the top; everything else is the same | "Contract", table (Item/Qty, Sensor/10), "Signatures", header "ACME" | "Inserted first." + the same content | Every unchanged paragraph, table cell and header knows its counterpart in both directions. The inserted paragraph has no counterpart. | `ReviewMarkerTests.Every_compared_paragraph_knows_its_counterpart_even_when_nothing_changed` | Auto |
| TC-ALN-04 | FR-04.2 | Paragraphs are rewritten in place | "Warranty: 12 months.", "Draft" | "Guarantee covers 24 months.", "Approved" | **No** "added" or "deleted" paragraphs. Word-level findings `12` → `24` and `Draft` → `Approved`. | `ReviewMarkerTests.Paragraphs_rewritten_in_place_still_show_their_changed_words` | Auto |

## 11. Review items and locations

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-RV-01 | FR-03.8, FR-10.1 | A document contains an equation | The same equation `x=a/b` in both files | The result is **not** "No differences found". A review item mentions "equation". | `ContentCoverageTests.An_equation_is_reported_for_review_instead_of_being_skipped` | Auto |
| TC-LOC-01 | FR-11.6 | Locations are given as a reader finds them | Paragraph, table (Item/Qty, Sensor/10), paragraph after the table, header "ACME" | Paragraph after the table: "Page unavailable, Paragraph 2" (table cells not counted). Header: "Header, Paragraph 1". Cell "10": "Page unavailable · Table 1, Row 2, Column 2 (Sensor · Qty)". No location: "Not present". | `ContentCoverageTests.Locations_name_the_part_and_count_paragraphs_as_a_reader_does` | Auto |

## 12. Sample documents

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-SMP-01 | FR-03 to FR-11 | Compare the two sample documents end to end (OCR replaced, so the result is the same on every system) | `samples/Sample-Reference-RevA.docx` and `samples/Sample-Current-RevB.docx` | Exactly the 21 findings listed in [samples/README.md](../samples/README.md), in order, with the change name and both values. No review items, and no page-number findings. Locations: "Header, Paragraph 1", "Footer, Paragraph 1", "Footnote 1, Paragraph 1", "Paragraph 12" for the body, and "Table 2, Row 4, Column 2 (Assay · Acceptance criteria)" for a cell. | `SampleDocumentTests.Sample_documents_report_exactly_the_planned_changes`, `SampleDocumentTests.Sample_findings_are_located_as_a_reviewer_finds_them_in_Word` | Auto |

## 13. Viewer (UI)

**TC-UI-01**: clicking changes in the real window. Covers FR-02.3, FR-05.3, FR-07.1, FR-08.2, FR-10.2 and FR-12 to FR-13.2.
**Automated test:** `ChangeSelectionTests.Clicking_a_table_value_or_image_region_focuses_its_exact_change_and_supports_reselection`. It also runs `ReviewMarkerTests.VerifyInWindow`.
**Status:** Auto. On Windows it uses the real desktop window. On macOS and Linux the same window is drawn off screen (Avalonia headless platform, same Skia renderer), because macOS only allows windows on a program's main thread.

| Step | Action | Expected result |
|---|---|---|
| 1 | Click the changed table value 10 → 15 in Reference | Details show 10 / 15, "Reference structure: Table", "READ FROM DOCX", "update Current", "Reference requires “10”; Current contains “15”", location with "Sensor" and "Qty". The list narrows to 1 item and **Show all changes** appears. |
| 2 | Click the image region 30 → 45 in Current | The image crop is shown, "REVIEW REQUIRED", "inspect the original evidence", "Reference: Page 8", "Current: Page 9". |
| 3 | Click the same image in Reference | The same finding is selected again. |
| 4 | Click **Hide highlights** | The button changes to "Show highlights". Image underlines are hidden, but clicking the image still selects the change. |
| 5 | Click **Show all changes**, then **Next** | The list shows all 3 findings. Next selects the table-structure finding. |
| 6 | Image replaced by a table: click it in Reference and in Current | Grey outline on both sides. Details: "Image recognition read '30'", "Reference value was found in an image", "Current value is in Table 1, Row 2, Column 2", list label "Image → Table". |
| 7 | A row is missing from Current | Pink marker in Reference, pink outline in Current. Details: "Sensor" / "Not present", "Missing from Current". |
| 8 | Alignment change | Pink underline. Details: "Alignment is wrong". |
| 9 | Subscript lost in "H₂O" | The "2" is drawn lowered in Reference. Details: "'2' should appear below the text line". |
| 10 | Space missing ("500 mg" → "500mg") | Details show "500 mg" / "500mg" and "missing a space"; the location starts "Both documents:". |
| 11 | Full sample document | Both panes are line-aligned, and the merged "Total" cell spans 2 columns. |
| 12 | Click a highlight (the list narrows to it), then click unchanged text | "No difference" is shown and the list shows all differences again. *(Regression: this sequence used to close the app.)* |

**TC-UI-02**: the "Show on the real pages" window (FR-16.6). Runs inside the same UI test as TC-UI-01 (`PageCompareWindowTests.Verify`), with prepared pages so it needs no Word or LibreOffice.

| Step | Action | Expected result |
|---|---|---|
| 1 | Open the window for "95.0% → 90.0%" | Both pages show one mark for it. |
| 2 | Press → | The next difference (text only in the approved document) is shown; the main window's selection follows; only the approved page has a mark. |
| 3 | Press ← | Back to the first difference. |

## 14. PDF report

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-RPT-01 | FR-15.1, FR-15.2 | Create a report | A result with 3 findings (text, subscript, deleted paragraph) | A non-empty PDF file is created. | `ComparisonReportGeneratorTests.Generate_creates_a_pdf_only_when_explicitly_called` | Auto |
| TC-RPT-02 | FR-11.6, FR-15.2–15.4 | Report for the sample documents: its **text** is read back from the PDF | Sample comparison (TC-SMP-01) | The PDF contains "Header, Paragraph 1", "Footnote 1, Paragraph 1", "[space]", "Row missing from Current" and "Text hidden in Current". It does **not** contain ", Run ", "Paragraph 54", "(empty)" or "Deleted paragraph 4". | `SampleDocumentTests.Sample_report_names_parts_shows_whitespace_and_uses_the_app_wording` | Auto |
| TC-RPT-03 | FR-15.3 | Values as printed in the report | " ", "  ", tab, "", "(no corresponding content)", "30" | "[space]", "[space][space]", "[tab]", "(empty)", "Not present", "30". | `ContentCoverageTests.Report_values_make_whitespace_and_absent_content_visible` (6 cases) | Auto |

---

## 14a. Exact pages

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-EXV-01 | FR-16.1 | PDFium reads a real PDF | The app's own PDF report | One A4 page (595 points wide), a picture with dark pixels on white, and its text "DOCX Comparison Report" with every character inside the page. | `ExactViewTests.Pdfium_draws_pages_and_reads_their_characters` | Auto |
| TC-EXV-02 | FR-16.2, FR-16.3 | Differences are found on a page | A made-up page with a header, a title, a short value "3.0" that also appears later, a symbol drawn without text, and a paragraph hidden in the document | "95.0%" is boxed at its exact position; "3.0" on its own line, not inside the longer paragraph; the header is found; "≤" gets a thin caret before "1.0%"; hidden text has no box; an added paragraph is a line above the paragraph it precedes. | `ExactViewTests.Differences_are_found_on_the_page_where_they_are_printed` | Auto |
| TC-EXV-03 | FR-16.1, FR-16.2, FR-16.5 | The sample documents laid out end to end | `samples/` pair, laid out with LibreOffice (chosen explicitly) | 2–4 pages per document, laid out by LibreOffice. Every difference is marked on each side where it exists, except text hidden in Current. The Assay value is on page 1; the image change on a later page; the inserted paragraph is a gap line in the approved document. | `ExactViewTests.Sample_documents_are_laid_out_and_every_visible_difference_is_marked` | Auto (LibreOffice) |
| TC-EXV-04 | FR-16.1a, FR-16.1b | The chosen layout program is used, never swapped | Word chosen while Word is switched off for the run; a settings file | No PDF and no engine; the reason says Word is "switched off"; the temporary copy is deleted. The layout choice defaults to Word, is saved and read back as LibreOffice, and a settings file saved before the choice existed reads as Word. | `ExactViewTests.The_chosen_layout_engine_is_used_or_its_problem_is_reported_without_switching_engines` | Auto |
| TC-EXV-05 | FR-16.1a, FR-16.1b | The "Laid out by" switch on screen | The sample pair in the main window, Word switched off for the run | The switch appears only in Exact pages, with Microsoft Word chosen. Both panes give the reason and a "Use LibreOffice instead" button; no pages are shown. Clicking it lays both documents out with LibreOffice ("laid out by LibreOffice"), marks the differences on both, checks the LibreOffice button and saves the choice. Choosing Word again drops the LibreOffice pages and shows the reason; choosing LibreOffice again shows the pages from the cache at once. Runs inside the shared UI test (`ExactViewTests.VerifyEngineSwitch`). | `ChangeSelectionTests.Clicking_a_table_value_…` | Auto (LibreOffice) |

## 14b. Comparison history

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-HIS-01 | FR-18.1 | A comparison is saved | The sample pair | One entry: file names, both SHA-256 fingerprints, reviewer, "21 differences · 1 to check yourself", "Row missing from Current" 3 times, and every difference with values and locations (e.g. 95.0% → 90.0% at "Table 2, Row 4, Column 2 (Assay · Acceptance criteria)"; a removed space as "[space]"). The Current file is "unchanged since". | `HistoryTests.A_comparison_is_saved_with_its_files_result_and_differences`, `HistoryTests.A_history_file_with_a_byte_order_mark_is_cleaned_when_the_next_comparison_is_saved` | Auto |
| TC-HIS-02 | FR-18.1, FR-18.4 | Duplicates, changed files, damage, delete, clear | The sample pair compared twice, then Current replaced | The second identical comparison is not saved again. After Current changes, a new entry comes first and the old entry says Current "changed". A damaged line is skipped. Delete removes one entry; Clear removes all; a deleted file shows as "missing". | `HistoryTests.Repeated_comparisons_are_saved_once_and_entries_can_be_deleted` | Auto |
| TC-HIS-03 | FR-18.3 | CSV export | Entries with commas, quotes, line breaks, "=SUM(A1)" and "-0.5%" | Commas and line breaks are quoted, quotes doubled, and formula-like values start with an apostrophe so Excel shows them as text. | `HistoryTests.History_exports_to_csv_safely` | Auto |
| TC-HIS-04 | FR-18.2, FR-18.3 | The History window | A history with the sample comparison | One entry listed; its details show the result, the file names, "Unchanged since this comparison" and "Row missing from Current 3"; "Compare these files again" hands both files to the main window. Runs inside the shared UI test (`HistoryTests.VerifyWindow`). | `ChangeSelectionTests.Clicking_a_table_value_…` | Auto |

## 14c. Choosing what to compare (headers and footers)

| ID | Req | Scenario | Input | Expected result | Automated test | Status |
|---|---|---|---|---|---|---|
| TC-OPT-01 | FR-19.1 | Headers and footers switched off | The sample pair | 19 findings instead of 21: the header ("Confidential") and footer ("Rev A") changes are gone; the footnote change remains. Neither document contains header or footer content; footnotes are still there. | `ComparisonOptionsTests.Without_headers_and_footers_their_changes_are_not_reported_and_they_are_not_shown` | Auto |
| TC-OPT-02 | FR-19.1 | Documents that differ only in the header | Header "Confidential" vs "Internal" | With headers on: Different. With headers off: "No differences found", zero findings. | `ComparisonOptionsTests.Documents_that_differ_only_in_the_header_match_when_headers_are_switched_off` | Auto |
| TC-OPT-03 | FR-19.2, FR-19.3 | The choice is recorded and remembered | The sample pair with headers off | The PDF scope says "Headers and footers were NOT compared"; the history entry has "headers and footers compared: No" (also in CSV), and the same files with headers on are a separate entry; the setting is saved and loaded (default on). | `ComparisonOptionsTests.Report_history_and_settings_record_that_headers_and_footers_were_not_compared` | Auto |
| TC-OPT-04 | FR-19.1–19.3 | The checkbox on screen | The sample pair in the main window | Unticking compares again at once: HEADER and FOOTER areas disappear, FOOTNOTE 1 stays, 19 findings, the status ends "headers & footers not checked", the setting is saved. Ticking it brings them back (21 findings). Runs inside the shared UI test (`ComparisonOptionsTests.VerifyCheckBox`). | `ChangeSelectionTests.Clicking_a_table_value_…` | Auto |

## 15. Manual test cases

Features that work today but have no automated test. Run them before each release.

| ID | Req | Steps | Expected result |
|---|---|---|---|
| TC-MAN-01 | FR-01.1, FR-01.2, FR-13.6 | 1. Start the app. 2. Check the **Compare** button. 3. Choose the approved Reference `.docx` in box ①. 4. Select a Current `.docx` with at least 2 changes. 5. Click Compare. | The picker shows only `.docx` files. Compare stays disabled until both files are chosen. After comparing, the first change in Current is selected and both panes scroll to it. |
| TC-MAN-02 | FR-12.5, FR-12.6 | 1. Compare two multi-page documents. 2. Scroll one pane. 3. Click Zoom + several times, then Zoom − several times. 4. Click a mark in the strip beside the scroll bar. | The other pane scrolls with it. Zoom stops at 180% and 70%. Clicking a mark selects that finding and scrolls to it. |
| TC-MAN-03 | FR-13.3, FR-10.2 | 1. Compare documents that include table, image and review findings. 2. Choose each filter in turn. 3. Click **Next to review** several times. | Each filter shows only its kind of finding. **Next to review** steps through review items only, showing "x of N". |
| TC-MAN-04 | FR-14.1, FR-14.2 | 1. Compare. 2. Scroll to the middle. 3. Change a value in Current in Word and save. | Within about 2 seconds the comparison refreshes, the new value appears, and the scroll position is kept. No error appears while Word is saving. |
| TC-MAN-05 | FR-15.2, FR-15.3 | 1. Compare documents that include a text, formatting, table, OCR and spacing change. 2. Click **Save PDF report** and save it. 3. Open the PDF. | The PDF contains file names, the result, counts, the scope text, a summary by change type, and one entry per finding with both values. OCR findings show their "verify" note. |
| TC-MAN-06 | NFR-04 | Run TC-MAN-01 on a computer **without OCR** (Linux, or Windows without an OCR language) with documents whose images differ. | Image changes are still reported as visual changes, with crops. The review note says text recognition is unavailable. No values are invented. |
| TC-MAN-07 | NFR-04 | Run TC-MAN-01 and TC-MAN-04 on **macOS** with the sample documents. | The app opens and compares, the image change reads "99.1 → 98.4" (Apple Vision OCR, marked verify), and saving Current in Word refreshes the comparison. |
| TC-MAN-08 | FR-12.7 | Open the app at its smallest size (1100 × 680) and full size, compare the sample documents, and ask someone new to the tool to find the Assay change and say what it means. | No text is cut off or split mid-word, nothing scrolls sideways, and the person can find and explain the change without help (compare with [docs/screenshots](screenshots/)). |
| TC-MAN-09 | FR-16 | 1. Compare the sample documents. 2. Click **Exact pages**. 3. Click several differences in the list, including "Text hidden in Current" and the image change. 4. Zoom to 160%. 5. Click **Reading view**. | After a few seconds both documents show as real pages ("laid out by LibreOffice" or "Microsoft Word"). Each difference is boxed on the page and both documents scroll to it. For hidden text, the Current pane says it is not visible on the pages. At 160% the print is readable and the panes scroll sideways. Reading view comes back unchanged. On Windows with Word installed, the pages say "laid out by Microsoft Word". |
| TC-MAN-10 | FR-17.1 | Click **Open in Word** above each document, then try to save in Word. | Word opens "… (read-only copy).docx"; saving asks for a new name (the copy is read-only), and the original file is unchanged. |
| TC-MAN-11 | FR-16.6 | Select a difference, click **Show on the real pages**, then press → several times, try − / +, Whole page and Zoom to change, and press Esc. | The window shows the approved and Current pages side by side, zoomed to the change with it outlined on both. → moves on and the main window's list follows. For hidden text, the Current side says it is not visible and shows the same area. For an inserted paragraph, the approved side shows a pink line where it would be. Esc closes the window. |
| TC-MAN-12 | FR-18 | 1. Compare the sample documents. 2. Click **History**. 3. Search for "RevB". 4. Click **Export all (CSV)** and **Export this comparison (CSV)** and open both in Excel. 5. Edit and save Current in Word, compare again, and open History. 6. Click **Compare these files again** on the older entry. 7. Delete one entry, then Clear history. | The comparison is listed with date, reviewer, files and result. The CSV files open in Excel with correct symbols (≤, µ). After step 5 there are two entries and the older one says Current "changed since". Step 6 loads both files in the main window and compares them. Clear asks for confirmation. |
| TC-MAN-13 | FR-16.1a, FR-16.1b | On a **Mac or Windows PC with activated Microsoft Word**: compare the samples, click **Exact pages**, keep **Laid out by: Microsoft Word**. Repeat with Word already open on one of your own documents. Then click **LibreOffice**, then **Microsoft Word** again. Finally once on a computer where Word is not activated. | Word closed: Word starts in the background (on a Mac the first time it may ask "Grant File Access" for its working folder: click Grant), the pages say "laid out by Microsoft Word", and Word quits afterwards. Word open: your document stays open and unchanged, Word keeps running, the pages say "laid out by Microsoft Word". LibreOffice: the pages are laid out again ("laid out by LibreOffice") and the marks move with the new page breaks; Word again is instant (reused). Not activated: both panes say "Microsoft Word could not save a PDF: Word may need to be activated…" with **Use LibreOffice instead**, and no LibreOffice pages appear until you click it. Restart the app: the last choice is kept. |

---

## 16. Known gaps and defects

These describe **what should happen**. Fixed gaps are marked ✅ and point to the test that now covers them. Gaps marked **Known-gap test** are automated in `KnownGapTests` and reported as Skipped until fixed. The rest are still proposals: write each test before its fix, so the fix is proven.

| ID | Req | Scenario | Reference | Current | Expected result | Today's behaviour |
|---|---|---|---|---|---|---|
| TC-GAP-01 | FR-03.6 | Text is hidden in Current | "Store below 25 °C" (visible) | Same text with **Hidden** formatting | A finding: "Hidden" Off → On. | ✅ **Fixed.** Covered by TC-FMT-06. |
| TC-GAP-02 | FR-03.7 | An inserted symbol changes | "Assay **≤** 2%" (≤ inserted as a Symbol-font character) | "Assay **≥** 2%" | Text finding `≤` → `≥`. | ✅ **Fixed.** Covered by TC-TXT-06. |
| TC-GAP-03 | FR-03.8 | An equation changes | Equation `x = a / b` | Equation `x = a × b` | A text finding, or at least a "Needs review: equation" item. | 🟡 **Partly fixed:** a "Needs review: equation" item is now reported (TC-RV-01). The equation content is still not compared. |
| TC-GAP-04 | FR-03.9 | List numbering changes | Numbered list 1, 2, 3 | Step 2 removed from numbering (plain paragraph) | A finding "Numbering removed". | No finding. **Known-gap test:** `KnownGapTests.Removed_list_numbering_is_reported` |
| TC-GAP-05 | FR-06.6 | A character style changes | "Warning" with character style **Strong** | "Warning" with no character style | A finding "Character style Strong → (none)". | No finding. **Known-gap test:** `KnownGapTests.Changed_character_style_is_reported` |
| TC-GAP-06 | FR-15.3 | PDF report with a removed space | "500␠mg" | "500mg" | The PDF shows Reference "␠" and Current "Not present". | ✅ **Fixed.** Covered by TC-RPT-02 and TC-RPT-03. |
| TC-GAP-07 | FR-13.4 | Previous / Next with a filter | Any documents with table and text findings | — | With filter "Table values and structure" on, **Next** moves only between table findings and the filter stays on. | Next jumps to text findings and the list silently resets to all findings while the filter still shows "Table…". |
| TC-GAP-08 | FR-13.5 | Findings order | A table change on page 1, a text change on page 5 | — | The list shows the page 1 table change first. | All text findings come first, then tables, then images, then review items. **Known-gap test:** `KnownGapTests.Findings_follow_document_reading_order` |
| TC-GAP-09 | FR-14.3 | Selecting another file during a comparison | — | — | The status shows the new state, or "Ready to compare". | The status stays on "Comparing text, tables and image content…". |
| TC-GAP-10 | FR-06.4 | Bold removed from a word that Word stored as 3 runs | "**Important**" (1 run) | "Important" (3 runs, not bold) | **One** "Bold" finding. | Three "Bold" findings. **Known-gap test:** `KnownGapTests.Bold_removed_from_a_word_stored_in_several_runs_is_one_finding` |
| TC-GAP-11 | FR-06.5 | Explicit default vs not set | Underline not set; colour not set | Underline `none`; colour `auto` | No findings. | ✅ **Fixed.** Covered by TC-FMT-07. |
| TC-GAP-12 | NFR-03 | Real Word-authored documents | A `.docx` saved by Microsoft Word (with spell-check marks, bookmarks, styles, theme) | A copy with one changed value, saved again in Word | Exactly one finding for the changed value. | Not tested: all current tests use small synthetic files. |
| TC-GAP-13 | FR-09.6 | Text near an image-to-table swap changes | Heading "Results", image of a table, note "Tested at 25 °C" | Heading "Results", table, note "Tested at **30 °C**" | Text finding `25` → `30`. | The note is reported as **deleted**, and the new text "30 °C" appears nowhere. **Known-gap test:** `KnownGapTests.Edited_note_next_to_an_image_to_table_swap_is_reported_as_changed` |
| TC-GAP-14 | FR-04.8 | A line is inserted at the top of a table cell | Cell lines: "Line A", "Line B" | Cell lines: "**New**", "Line A", "Line B" | One "added" finding for "New". | Every line in the cell is reported as changed. **Known-gap test:** `KnownGapTests.Line_inserted_at_the_top_of_a_cell_is_one_added_paragraph` |
| TC-GAP-15 | NFR-07 | Large document performance | A 300-page document with 50 tables and 30 images | Same with 20 changes | Comparison finishes, and clicking a finding responds within the agreed target (target not defined yet). | Not measured. Expected to slow down, because every click rebuilds both views. |

---

## 17. Coverage by requirement

| Requirement | Test cases |
|---|---|
| FR-01 Input | TC-IN-01, TC-MAN-01 |
| FR-02 Overall result | TC-RES-01, TC-RES-02, TC-MIX-03, TC-UI-01 |
| FR-03 Content read | TC-TXT-01…06, TC-RD-01…05, TC-TBL-06, TC-PRT-01, TC-PRT-04, TC-FMT-06, TC-RV-01, TC-SMP-01, TC-GAP-04 |
| FR-04 Pairing | TC-ALN-01…04, TC-TBL-03, TC-TBL-04, TC-TBL-07, TC-IMG-07, TC-PRT-04, TC-GAP-14 |
| FR-05 Text | TC-TXT-01…05, TC-UI-01 |
| FR-06 Formatting | TC-FMT-01…07, TC-GAP-05, TC-GAP-10 |
| FR-07 Tables | TC-TBL-01…05, TC-TBL-08, TC-UI-01 |
| FR-08 Images | TC-IMG-01…08, TC-MAN-06 (FR-08.7 untested) |
| FR-09 Image ↔ table | TC-MIX-01…07, TC-GAP-13 |
| FR-10 Review items | TC-ALN-02, TC-RV-01, TC-RD-05, TC-UI-01, TC-MAN-03 |
| FR-11 Page location and locations | TC-PG-01…06, TC-PRT-02, TC-PRT-03, TC-LOC-01, TC-SMP-01 |
| FR-12 Viewer | TC-UI-01, TC-MAN-02, TC-MAN-08 |
| FR-13 Findings list | TC-UI-01, TC-MAN-01, TC-MAN-03, TC-GAP-07, TC-GAP-08 |
| FR-14 Auto refresh | TC-MAN-04, TC-GAP-09 |
| FR-15 PDF report | TC-RPT-01…03, TC-MAN-05 |
| FR-16 Exact pages | TC-EXV-01…05, TC-UI-02, TC-MAN-09, TC-MAN-11, TC-MAN-13 |
| FR-17 Open the original | TC-MAN-10 |
| FR-18 Comparison history | TC-HIS-01…04, TC-MAN-12 |
| FR-19 Choosing what to compare | TC-OPT-01…04 |
| NFR-03, NFR-04, NFR-07 | TC-GAP-12, TC-MAN-06, TC-MAN-07, TC-GAP-15 |

**Not covered by any test:** FR-01.3 error cases other than "file in use", FR-08.7 (image size, alt text, title, alignment and placement changes), most review-item types in FR-10.1 (text box, block content control, tracked changes, and so on), and NFR-09 (dependency vulnerabilities; check with `dotnet list package --vulnerable`).

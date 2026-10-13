# Sample documents

Two **synthetic** Word documents for trying the tool and for manual testing (see [docs/TEST_CASES.md](../docs/TEST_CASES.md)). They contain no real product or company data, so they are kept in git. All other `.docx` files stay ignored.

| File | Role |
|---|---|
| `Sample-Reference-RevA.docx` | **Reference**: the approved version (Rev A) |
| `Sample-Current-RevB.docx` | **Current**: the version to check (Rev B), with planned changes |

Both are a 2-page "Finished Product Specification" with a title, headings, two tables (one with a merged "Total" cell), a stability image, a content control, a footnote, a header, and a footer with page-number fields.

The automated test **TC-SMP-01** (`SampleDocumentTests`) compares these two files on every test run and checks the exact list below. If you change the files or the tool's behaviour, update this table and the test together.

## How to use

1. Start the app: `dotnet run` from the project folder.
2. In **① Approved (Reference)**, click **Choose…** and pick `samples/Sample-Reference-RevA.docx`.
3. In **② To check (Current)**, click **Choose…** and pick `samples/Sample-Current-RevB.docx`.
4. Click **Compare**. The two documents appear side by side, with every difference highlighted and listed on the right.
5. Optional: click **Exact pages** to see the real pages (laid out by Word or LibreOffice) with the differences marked on them, or **Open in Word** to open a read-only copy.

## Expected result

Status: **"21 differences found · 1 to check yourself"** (the image change, because values read from an image must be verified).
- **Windows and macOS:** the image change is read by the computer's own OCR (Windows OCR or Apple Vision) and shown as "Possible text change in image": `99.1` → `98.4`.
- **Linux:** it is shown as "Visual image change" with crops, because Linux has no built-in OCR.

| # | Planned change (Reference → Current) | Where | Expected finding |
|---|---|---|---|
| 1 | "Sample Pharma Ltd — **Confidential**" → "— **Internal Use**" | Header | Text changed: "Confidential" → "Internal Use", located at "Header, Paragraph 1" |
| 2 | Title alignment centre → left | Title | Text alignment changed: center → left |
| 3 | New paragraph "This specification applies to commercial batches only." | Top of document, after the intro | Inserted paragraph |
| 4 | "250 mg" → "250mg" (space removed) | 1. Product description | Space missing in Current |
| 5 | C₉H₈O₄ → C₉H8O₄ (subscript lost on "8") | 1. Product description | Subscript removed |
| 6 | "**Warning:**" bold → not bold | 1. Product description | Bold changed: On → Off |
| 7 | "Keep out of reach of children." is **hidden** in Current | 1. Product description | Text hidden in Current (Hidden: Off → On). The viewer shows it faded. |
| 8 | Microcrystalline cellulose 120.0 → 125.0, Total 373.0 → 378.0 | Table 1 (Composition) | Two text changes with row and column |
| 9 | Assay 95.0% – 105.0% → 90.0% – 110.0% | Table 2 (Specification) | Two text changes: `95.0%` → `90.0%`, `105.0%` → `110.0%`, at "Table 2, Row 4, Column 2 (Assay · Acceptance criteria)" |
| 10 | Dissolution "30 minutes" → "45 minutes" | Table 2 | Text change `30` → `45` |
| 11 | "Water content / NMT 2.0% w/w / AM-104" row deleted | Table 2 | "Row missing from Current" for each of its 3 cells, with no separate row-count finding |
| 12 | "Limit for total impurities: **≤** 1.0%" → "**≥** 1.0%" (Symbol-font character) | After Table 2 | Text change `≤` → `≥` |
| 13 | "within **30** days" → "within **45** days" | After Table 2 | Text change `30` → `45`, at "Paragraph 12" (table cells are not counted) |
| 14 | Footnote "SP-**2024**-01" → "SP-**2025**-01" | Footnote 1 | Text change, located at "Footnote 1, Paragraph 1" |
| 15 | Stability image: 6-month assay 99.1 → 98.4 | 4. Stability | **One** image finding. **Windows and macOS:** "Possible text change in image" `99.1` → `98.4`, marked verify. **Linux:** "Visual image change" with crops. (The automated test replaces OCR, so it always expects the visual change.) |
| 16 | Paragraph "Retain samples must be stored…" deleted | 4. Stability | Deleted paragraph |
| 17 | Footer "Rev **A**" → "Rev **B**" | Footer | Text change `A` → `B` |

With **Include headers & footers** switched off, the result is **19 differences** (items 1 and 17 are not compared) and the status ends "headers & footers not checked".

### Checks that must produce no finding

| # | Content | Expected |
|---|---|---|
| C1 | "Batch size: 1,000,000 tablets" inside a content control, the same in both files | **No finding.** Text in an inline content control is read and compared like other text. |
| C2 | Footer "Page 1 of 2" (page-number fields) | **No finding.** Page fields are compared as fields, not as printed numbers. |

**Totals:** 17 planned changes, all detected, giving 21 findings (items 8, 9 and 11 give more than one finding each). 2 checks produce no finding.

## Source check sample (`source-check/`)

A synthetic question-based QOS for penicillamine (public data, no company content) and the source PDFs its answers come from, in an eCTD-like folder. Some answers differ from the sources on purpose. The files are generated by `SourceCheckSamples` in the tests; test **TC-SRC-10** checks the result below.

| File | Role |
|---|---|
| `source-check/QOS-Sample.docx` | The document to check: 3 questions (2.3.S.1, 2.3.S.4, 2.3.S.7) with tables and sentences |
| `source-check/m3/32s-drug-sub/…` | 4 source PDFs: nomenclature, specification, batch analyses, stability summary |

**How to use:** click **Source check** → **Source folders…** → **New source folder** (any name) → **Upload folder…** and choose `samples/source-check/m3` → Close. Choose `QOS-Sample.docx` in ②, the folder in ③, and click **Proceed checking**.

**Expected result:** "3 questions, 16 facts checked: 10 match, 2 different, 1 not found, 3 to check yourself."

| Fact in the QOS | Source | Expected |
|---|---|---|
| Total impurities NMT 0.2% (AM-102) | specification.pdf: NMT 0.15% | ✗ Different in source |
| A retest period of 36 months | stability-summary.pdf: "Proposed retest period: 24 months" | ✗ Different in source |
| Particle size D90 NMT 150 µm (AM-110) | not in any PDF | ⚠ Not found in sources |
| Molecular Weight 149.21 g/mol | nomenclature.pdf: "Relative molecular mass 149.21" (another label) | ? Check yourself |
| "Based on 24 months long-term data at 25 °C/60% RH" | stability-summary.pdf (values found together, no label) | ? Check yourself |
| "…stored in tight, light-resistant containers…" (no values) | stability-summary.pdf (passage with the same words) | ? Check yourself |
| The other 10 (names, formula, CAS number, class, assay, loss on drying, residue on ignition, batch numbers) | as in the sources | ✓ Matches source |

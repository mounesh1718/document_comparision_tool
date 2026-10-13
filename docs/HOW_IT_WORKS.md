# How Document Checker finds differences

Seven flow diagrams, from the click on **Compare** to the screen, with the package or component that does each step. The diagrams use [Mermaid](https://mermaid.js.org); GitHub and GitLab draw them, and VS Code does with a Mermaid preview extension.

| Package / component | Used in |
|---|---|
| **DocumentFormat.OpenXml** 3.5.1 | Reading the `.docx` (diagram 2, images in diagram 4) |
| **DiffPlex** 1.9.0 | Text and formatting differences (diagram 3), words in images (diagrams 4 and 5) |
| **SixLabors.ImageSharp** 2.1.13 | Pixel comparison, enlarging images for OCR, crops (diagram 4) |
| **Windows OCR** / **Apple Vision** | Reading text in images (diagrams 4 and 5) |
| **System.Security.Cryptography** (SHA-256) | Exact File Match, pairing images, history |
| **Avalonia** 12.1.3 | The screen, including drawing Reading view (diagram 7) |
| **Microsoft Word** or **LibreOffice** (the reviewer's choice) | Laying out the real pages for Exact pages (diagram 6) |
| **PDFium** (bblanchon.PDFium 156.0.8066) | Page pictures and character positions for Exact pages (diagram 6) |
| **PDFsharp-MigraDoc** 6.2.4 | The PDF report |
| **System.Text.Json** | History, OCR results |

## 1. The whole flow

```mermaid
flowchart TD
  U["Reviewer chooses the Reference and Current .docx<br/>and clicks Compare"] --> S["DocumentComparisonService.Compare<br/>runs in the background"]
  S --> R1["Read Reference<br/>DocumentReader · OpenXML SDK"]
  S --> R2["Read Current<br/>DocumentReader · OpenXML SDK"]
  R1 --> OK{"Both files readable?"}
  R2 --> OK
  OK -->|no| BAD["Clear message:<br/>file in use, not a .docx, damaged…"]
  OK -->|yes| H["SHA-256 fingerprint of both files<br/>System.Security.Cryptography"]
  H --> SAME{"Same bytes?"}
  SAME -->|yes| EX["Exact File Match<br/>nothing else to compare"]
  SAME -->|no| SC["Leave out headers and footers<br/>when 'Include headers & footers' is off"]
  SC --> MIX["1. Image replaced by a table?<br/>ImageTableComparer + OCR (diagram 5)"]
  MIX --> P["2. Pair and compare paragraphs<br/>text · formatting · paragraph settings (diagram 3)"]
  P --> T["3. Pair and compare tables<br/>rows · columns · cells (diagram 3)"]
  T --> I["4. Pair and compare images<br/>pixels + OCR (diagram 4)"]
  I --> RV["5. Add 'check yourself' items<br/>content that cannot be compared"]
  RV --> RES["ComparisonResult<br/>status + list of findings"]
  RES --> UI["Screen · Avalonia<br/>Reading view, Exact pages, details panel"]
  RES --> PDF["PDF report · PDFsharp-MigraDoc<br/>only when asked"]
  RES --> HIS["History · System.Text.Json<br/>history.jsonl"]
```

## 2. Reading a .docx

A `.docx` is a zip file of XML parts. The OpenXML SDK opens it read-only and the reader turns each part into a clean model.

```mermaid
flowchart TD
  F[".docx = zip of XML parts"] --> O["OpenXML SDK opens it read-only<br/>WordprocessingDocument.Open(path, false)"]
  O --> B["word/document.xml<br/>the body, in order"]
  O --> HF["header*.xml and footer*.xml<br/>per section: default, first page, even pages"]
  O --> FN["footnotes.xml and endnotes.xml<br/>numbered in the order they are referenced"]
  O --> MED["word/media/*<br/>image bytes, found through relationships"]
  B --> PAR["Paragraph (w:p)"]
  B --> TBL["Table (w:tbl)"]
  B --> OTH["Other blocks<br/>content control, custom XML…"]
  PAR --> RUN["Runs (w:r) become TextRun<br/>text from w:t, tab, line break, non-breaking hyphen<br/>w:sym symbols become real characters (≤ ≥ ± µ)"]
  PAR --> FMT["Run formatting (w:rPr) becomes RunFormatting<br/>font, size, bold, italic, underline, strikethrough,<br/>sub/superscript, colour, highlight, spacing, hidden"]
  PAR --> LAY["Paragraph settings (w:pPr) become ParagraphLayout<br/>style, alignment, indents, spacing, page break before"]
  PAR --> IMG["Pictures (w:drawing) become DocumentImage<br/>see diagram 4"]
  PAR --> UNS{"Text box, equation, tracked change,<br/>floating image, chart, SmartArt?"}
  UNS -->|yes| REVN["Review item: check this yourself"]
  TBL --> CELL["Rows, cells and their paragraphs<br/>column span (gridSpan), row span (vMerge),<br/>nested tables read as lines of the cell"]
  OTH --> REVN
  HF --> FLD["PAGE, NUMPAGES, DATE fields become<br/>[Page number], [Page count], [Date/time]"]
  B --> PG["DocxPageMap: page numbers from<br/>Word's saved page markers or manual breaks"]
  RUN & FMT & LAY & IMG & CELL & REVN & FLD & PG & FN --> DM["DocumentModel<br/>numbered paragraphs, tables, images, parts, review items"]
```

## 3. Identifying the differences

Everything is paired first, so that one inserted line does not make every line after it look changed. Each pair is then compared in detail.

```mermaid
flowchart TD
  M["Two DocumentModels"] --> PART["Split into areas: body, each header, each footer, each note<br/>an area is only compared with the same area"]
  PART --> KEY["Paragraph key = its text + fingerprints of its pictures"]
  KEY --> LCS["Longest common subsequence of the keys<br/>identical paragraphs anchor the pairing"]
  LCS --> GAP["Between two anchors: similarity of each pair<br/>shared words (Dice), characters for 2 words or fewer"]
  GAP --> PAIRQ{"Similar (≥ 40%)?"}
  PAIRQ -->|yes| PAIRED["Paired: compared in detail"]
  PAIRQ -->|"no, but both sides have leftovers"| PAIRED
  PAIRQ -->|"only on one side"| ADDDEL["Paragraph added / deleted<br/>with the place it belongs in the other document"]
  PAIRED --> TXT["Text · TextChangeAnalyzer + DiffPlex<br/>character diff, widened to the whole value<br/>(MD-4482 → MD-4482-R), touching changes joined"]
  PAIRED --> FM["Formatting · FormattingAlignment + DiffPlex<br/>characters lined up first, then 12 run properties<br/>compared per stretch ('none', 'auto' = not set)"]
  PAIRED --> PL["Paragraph settings<br/>style, alignment (start = left), indents, spacing, page break"]
  M --> TP["Tables · paired by their whole text<br/>(same anchoring + similarity)"]
  TP --> TR["Rows · TableAlignment<br/>by first-column label when unique, else by whole row"]
  TR --> TC["Columns · by header row when headers are unique"]
  TC --> CE["Each cell: the same text and formatting checks"]
  TR --> MR["Row or column on one side only<br/>'Row missing from Current', value by value"]
  M --> IM["Images · diagram 4"]
  M --> RI["Review items · 'Needs review' findings"]
  TXT & FM & PL & ADDDEL & CE & MR & IM & RI --> DD["One DocumentDifference per change<br/>ID · type · Reference value · Current value ·<br/>location (paragraph, table, row, cell, page) · character range"]
  DD --> CR["ComparisonResult"]
```

The **character range** of each finding (paragraph, first character, length) is what lets the screen underline exactly the changed value, and Exact pages box it on the real page.

## 4. Extracting and comparing images

```mermaid
flowchart TD
  subgraph Extract["Extract · DocumentReader + OpenXML SDK"]
    W["w:drawing inside a run"] --> BL["a:blip r:embed<br/>relationship id, e.g. rId7"]
    BL --> REL["Relationship of the part that holds the run<br/>body, header, footer or note"]
    REL --> IP["ImagePart word/media/image1.png<br/>the image bytes"]
    W --> EXT["wp:extent cx, cy<br/>displayed size in EMU"]
    W --> DOC["wp:docPr descr, title<br/>alt text and title"]
    W --> PGI["DocxPageMap<br/>the page it is on"]
    IP & EXT & DOC & PGI --> DI["DocumentImage"]
  end
  DI --> PAIR["Pair the images of each area<br/>by SHA-256 fingerprint, then by paragraph pairing"]
  PAIR --> ONE{"Only on one side?"}
  ONE -->|yes| ADDIMG["Image added / missing"]
  ONE -->|no| HASH{"Same fingerprint?"}
  HASH -->|yes| PROPS["Compare size, alt text, title,<br/>alignment and position only"]
  HASH -->|no| PIX["ImageDifferenceAnalyzer · ImageSharp<br/>different size: whole image changed<br/>else compare pixels, a colour change over 24/255 counts"]
  PIX --> REG["Group changed pixels into regions (flood fill)<br/>join regions close together: one value, one region"]
  REG --> OCR["Read the text of both images<br/>SystemImageTextReader"]
  OCR --> WIN["Windows<br/>PowerShell + Windows.Media.Ocr"]
  OCR --> MAC["macOS<br/>osascript + Apple Vision"]
  OCR --> LIN["Linux<br/>no OCR"]
  WIN & MAC --> WORDS["Words with their boxes in pixels<br/>image enlarged up to 2× for small text<br/>results cached by fingerprint"]
  WORDS --> ICC["ImageContentComparer<br/>charts: values matched to their category label<br/>other text: words grouped into rows,<br/>rows paired by height, DiffPlex word diff per row"]
  ICC --> ONLY{"Does the changed word overlap<br/>changed pixels?"}
  ONLY -->|yes| TXTIMG["'Possible text change in image'<br/>e.g. 99.1 → 98.4, marked 'verify'"]
  ONLY -->|no| IGN["Ignored<br/>OCR misread of identical pixels"]
  REG --> LEFT["Changed region without a recognised word change<br/>'Visual image change', check the crop"]
  LIN --> LEFT
  TXTIMG & LEFT & ADDIMG & PROPS --> DD2["DocumentDifference<br/>with image ids and regions"]
  DD2 --> CROP["Screen: ImageSharp cuts enlarged crops<br/>the regions are marked on the image"]
```

## 5. An image replaced by a table

```mermaid
flowchart TD
  S1["Split each document into sections<br/>at paragraphs with a Heading style"] --> S2{"Same unique heading on both sides,<br/>one side: 1 image and no table,<br/>other side: no image and 1+ tables?"}
  S2 -->|no| NORMAL["Compared normally (diagrams 3 and 4)"]
  S2 -->|yes| REP["Finding: 'Image / table representation'<br/>always marked 'verify'"]
  REP --> O2["Read the image with OCR<br/>Windows OCR or Apple Vision"]
  O2 --> FAIL{"Text recognised?"}
  FAIL -->|no| NOVAL["No values claimed<br/>'check the image and table manually'"]
  FAIL -->|yes| ROWS["ImageTableRows<br/>for each table row: find its first-column label<br/>in the image (it must be unique), take the words<br/>on that line, split them into cells at the widest gaps"]
  ROWS --> MAPPED{"Cells mapped?"}
  MAPPED -->|yes| CMP["Each cell value vs the recognised value<br/>'Image / table value (OCR)' with image box and cell"]
  MAPPED -->|"no, simple one-row table"| ORDER["Words compared in reading order (DiffPlex)"]
  MAPPED -->|"no, complex table"| AMB["'Could not reliably align'<br/>no values paired"]
```

## 6. Exact pages: marking the differences on the real pages

Exact pages does not find differences. It takes the findings already made from the `.docx` (diagram 3) and marks them on the pages as Word or LibreOffice lays them out. The reviewer chooses the program with the **Laid out by: Microsoft Word | LibreOffice** switch; the tool uses exactly that program and never swaps it silently.

```mermaid
flowchart TD
  C["Comparison already done<br/>findings with their character ranges"] --> CL["Reviewer clicks 'Exact pages'<br/>or 'Show on the real pages'"]
  CL --> ENG["Layout program chosen with 'Laid out by'<br/>Microsoft Word or LibreOffice<br/>remembered in settings.json"]
  SW["Reviewer switches the program"] -->|"pages of the other program dropped,<br/>laid out again"| ENG
  ENG --> CACHE{"Pages already made for this file<br/>with this program?<br/>SHA-256 fingerprint + program"}
  CACHE -->|yes| LOC
  CACHE -->|no| COPY["Copy the .docx to a temporary folder<br/>the layout program never opens the original"]
  COPY --> WINQ{"Which program was chosen?"}
  WINQ -->|"Word, on Windows"| WORD["Microsoft Word, invisible, through PowerShell<br/>open read-only, export as PDF"]
  WINQ -->|"Word, on a Mac"| MACW["Microsoft Word through AppleScript<br/>copy kept in a fixed folder: 'Grant File Access' asked once<br/>download flag removed from the copy only<br/>opened in the background, saved as PDF, the copy closed<br/>Word quit only if the tool started it"]
  WINQ -->|"LibreOffice"| LO["LibreOffice, no window<br/>soffice --headless --convert-to pdf<br/>with its own profile in the temporary folder"]
  WORD --> GOT{"PDF produced?"}
  MACW --> GOT
  LO --> GOT
  GOT -->|no| MSG["The reason in both panes<br/>for example Word not activated, or not installed<br/>+ button 'Use LibreOffice instead' or<br/>'Use Microsoft Word instead'"]
  MSG -->|"reviewer clicks it"| SW
  GOT -->|yes| PDFB["PDF of the real pages"]
  PDFB --> PDFIUM["PDFium · bblanchon.PDFium<br/>for each page: size in points,<br/>a picture of the page at 144 dpi,<br/>every character with its box,<br/>every picture with its box"]
  PDFIUM --> LOC["PageLocator<br/>match the document's paragraphs to the page's characters:<br/>spaces and line breaks ignored,<br/>long unique paragraphs anchor the match,<br/>short ones searched only between anchors,<br/>symbols missing from the PDF text tolerated"]
  LOC --> RECT["Each finding becomes boxes on a page<br/>text: its characters, one box per line<br/>table finding: a box round the table<br/>image finding: the picture's box + the changed area scaled<br/>content only in the other document: a pink line where it belongs<br/>hidden text: no box, the pane says so"]
  RECT --> SHOW["Exact pages screen<br/>page pictures with a transparent layer of marks on top<br/>click a mark to select the finding, zoom enlarges the pages"]
  RECT --> DLG["'Show on the real pages' window<br/>both pages side by side, only that finding, zoomed to it"]
  SHOW --> DEL["When the app closes<br/>the temporary copies and PDFs are deleted"]
  DLG --> DEL
```

## 7. Reading view: drawing the two documents side by side

Reading view does not show a picture of the Word file. It draws each document from the same data that was compared (the `DocumentModel`), so every highlight lands on exactly the characters of a finding. It is a clean reading layout, not Word's page layout (for that, see diagram 6).

```mermaid
flowchart TD
  R["ComparisonResult<br/>two DocumentModels + the findings"] --> RD["RenderDocuments<br/>clear both panes, release old pictures"]
  RD --> L["RenderDocument for the Reference<br/>left pane"]
  RD --> RT["RenderDocument for the Current<br/>right pane"]
  L & RT --> ORDER["Blocks in reading order<br/>headers, body, footers, footnotes, endnotes<br/>(headers and footers left out when switched off)"]
  ORDER --> BT{"Kind of block"}
  BT -->|paragraph| PP["A wrapping line of text pieces<br/>each run cut at every finding's character range (SplitRun)<br/>formatting drawn: size, bold, italic, underline, strikethrough,<br/>colour, sub/superscript moved down/up, hidden text faded<br/>alignment, indents, spacing, headings larger"]
  BT -->|table| TT["A grid with equal columns<br/>merged cells span their rows and columns<br/>rows lined up with the paired table,<br/>a row only in the other table becomes a pink placeholder row"]
  BT -->|picture| IM["The picture from the file's bytes, scaled to fit<br/>clickable boxes over its changed areas"]
  BT -->|"header, footer, note"| PT["A grey labelled area<br/>HEADER, FOOTER, FOOTNOTE 1"]
  BT -->|"content only in the other document"| GAP["A pink placeholder<br/>'Missing here' or 'Added in Current'"]
  PP --> MARK["Pieces that belong to a finding get a marker<br/>red underline: approved value · green underline: current value<br/>pink box: formatting or missing · pink caret: removed text<br/>side bar: whole paragraph · amber: check yourself"]
  TT & IM & PT & GAP --> MARK
  MARK --> ANC["Each marker is registered under its finding ID<br/>click a marker: that finding is selected<br/>select a finding: both panes scroll to it"]
  ANC --> ALIGN["AlignPanes, after the layout is measured<br/>the same content has the same key on both sides<br/>space is added above whichever side is higher<br/>placeholders take the height of the missing content<br/>paired table rows get equal heights<br/>both pages end at the same height"]
  ALIGN --> SYNC["Both panes scroll together<br/>a strip beside each scroll bar marks every finding"]
```

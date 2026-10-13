# Document Checker

A desktop tool that compares a **Current** Word document (`.docx`) against an approved **Reference** document and shows every difference side by side: text, spacing, formatting, tables, images (with text recognition), headers, footers and footnotes. It runs locally, never changes the files, and can save a PDF report and a history of every comparison.

| Document | What it covers |
|---|---|
| [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) | What the tool must do, with the status of each requirement |
| [docs/TEST_CASES.md](docs/TEST_CASES.md) | Every automated and manual test case |
| [COMPARISON_COVERAGE.md](COMPARISON_COVERAGE.md) | What is checked, and what to check yourself |
| [samples/README.md](samples/README.md) | Two sample documents and the expected result |
| [docs/screenshots](docs/screenshots/) | What the screens look like |

## Run it

1. Install the **.NET 10 SDK** (10.0.401 or later) from <https://dotnet.microsoft.com/download>.
2. From this folder:

   ```
   dotnet run
   ```

3. Choose the approved document in **① Approved (Reference)**, the document to check in **② To check (Current)**, and click **Compare**. To try it, use the two files in `samples/`.
4. Click **Exact pages** to see the real pages with the differences marked. **Laid out by: Microsoft Word | LibreOffice** chooses the program that lays them out; switching lays the pages out again with that program.

Run the tests:

```
dotnet test tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj
```

The expected result on Windows or macOS with LibreOffice installed is **101 passed, 0 failed, 6 skipped**. The 6 skipped tests describe known gaps (see [docs/TEST_CASES.md](docs/TEST_CASES.md)).

## SDK and platform

| Item | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401 | Projects use `Microsoft.NET.Sdk`, target framework `net10.0`, C# with nullable reference types |
| Operating systems | Windows, macOS, Linux | Text recognition in images needs Windows or macOS (see below) |

## Packages used by the app

From [DocumentChecker.Engine.csproj](DocumentChecker.Engine.csproj).

| Package | Version | Licence | Used for |
|---|---|---|---|
| [DocumentFormat.OpenXml](https://www.nuget.org/packages/DocumentFormat.OpenXml) (Microsoft Open XML SDK) | 3.5.1 | MIT | Reading the `.docx` files: text, formatting, tables, images, headers, footers and notes |
| [DiffPlex](https://www.nuget.org/packages/DiffPlex) | 1.9.0 | Apache-2.0 | Finding which characters and words changed between two texts |
| [SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp) | 2.1.13 | Apache-2.0 | Comparing images pixel by pixel, preparing images for text recognition, cutting crops — **see the security note below** |
| [Avalonia](https://www.nuget.org/packages/Avalonia), Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.3 | MIT | The desktop window on Windows, macOS and Linux. Draws with **SkiaSharp** 3.119.4, which comes with Avalonia. |
| [PDFsharp-MigraDoc](https://www.nuget.org/packages/PDFsharp-MigraDoc) | 6.2.4 | MIT | Creating the PDF report |
| [bblanchon.PDFium.Win32 / .macOS / .Linux](https://www.nuget.org/packages/bblanchon.PDFium.macOS) | 156.0.8066 | Apache-2.0 | Exact pages: drawing laid-out pages and reading the position of each character (PDFium is the PDF engine used by Chrome) |

## Packages used only by the tests

From [tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj](tests/DocumentChecker.Engine.Tests/DocumentChecker.Engine.Tests.csproj).

| Package | Version | Licence | Used for |
|---|---|---|---|
| [xunit](https://www.nuget.org/packages/xunit) | 2.9.3 | Apache-2.0 | Test framework |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) | 3.1.4 | Apache-2.0 | Running the tests from `dotnet test` and editors |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 17.14.1 | MIT | Test host for `dotnet test` |
| [PdfPig](https://www.nuget.org/packages/PdfPig) | 0.1.16 | Apache-2.0 | Reading the generated PDF report back to check its text |
| [Avalonia.Headless](https://www.nuget.org/packages/Avalonia.Headless) | 12.1.3 | MIT | Running the screen tests off screen |

## Parts of .NET used

| Part | Used for |
|---|---|
| `System.Security.Cryptography` (SHA-256) | File and image fingerprints: Exact File Match, pairing images, the history |
| `System.Text.Json` | Reading text-recognition results; saving the comparison history |
| `System.Diagnostics.Process` | Running text recognition, Microsoft Word and LibreOffice in the background |
| `FileSystemWatcher` | Comparing again automatically when a document is saved |

## Programs on the computer that the tool uses

These are not packages. The tool uses them when they are installed and keeps working without them.

| Program | Used for | If it is missing |
|---|---|---|
| **Windows OCR** (`Windows.Media.Ocr`, run through Windows PowerShell) | Reading text in images on Windows. Needs an installed OCR language. | Image changes are shown as visual changes with crops, without the values |
| **Apple Vision** (run through `osascript`, built into macOS 10.15+) | Reading text in images on macOS | As above |
| **Microsoft Word** (COM automation on Windows, AppleScript on macOS) | Laying out **Exact pages** exactly as Word prints them, when **Laid out by: Microsoft Word** is chosen (the default); **Open in Word**. Word must be activated to save PDFs. On macOS it asks once to "Grant File Access" to its working folder; only the tool's copy is opened and closed, and Word is quit only if the tool started it. | Exact pages gives the reason and a **Use LibreOffice instead** button; Open in Word uses the default app for `.docx` |
| **LibreOffice** (`soffice --headless`, free, any system) | Laying out **Exact pages** when **Laid out by: LibreOffice** is chosen (the only choice that works on Linux). `DOCHECK_LAYOUT_ENGINE=libreoffice` switches Word off for a run (the tests use it). | Exact pages gives the reason and, if Word is installed, a **Use Microsoft Word instead** button; everything else works |

## Notes

- **Security:** SixLabors.ImageSharp 2.1.13 has 3 high and 2 moderate known vulnerabilities (the build lists them as warnings NU1902/NU1903). It reads every image in the documents, so it should be upgraded. ImageSharp **3.x** uses the Six Labors Split License, which needs a paid licence for larger companies, so check first whether a patched **2.1.x** release exists. List vulnerable packages with `dotnet list package --vulnerable`.
- **Licences:** apart from the ImageSharp 3.x caveat, all packages are permissively licensed (MIT or Apache-2.0). LibreOffice is free (MPL-2.0). Word, Windows OCR and Apple Vision come with Office or the operating system.
- **Local data:** documents are only read. Exact pages and Open in Word work on temporary copies, which are deleted when the app closes. The comparison history and settings (`settings.json`: whether headers and footers are compared, and which program lays out Exact pages) are stored for each user on this computer (macOS: `~/Library/Application Support/DocumentChecker/history.jsonl`; Windows: `%LOCALAPPDATA%\DocumentChecker\history.jsonl`) and can be cleared in the History window.
- **Confidential documents:** `.gitignore` keeps every `.docx` out of git, except the synthetic files in `samples/`.

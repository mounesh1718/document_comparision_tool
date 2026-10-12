using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>
/// A test that uses the operating system's own text recognition: Windows OCR on Windows, Apple Vision on macOS.
/// Elsewhere (Linux) it is reported as skipped, not as passed, so a green run never hides that it did not run.
/// </summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Skip = "Requires the text recognition built into Windows or macOS.";
    }
}

/// <summary>
/// A test for a known gap or defect (TC-GAP-xx in docs/TEST_CASES.md). It states the correct behaviour and fails
/// until the gap is fixed, so it is skipped by default and shown as "Known gap" in every run.
/// Set DOCHECK_RUN_KNOWN_GAPS=1 to run these tests and see which gaps are still open.
/// When a gap is fixed, change the attribute to [Fact].
/// </summary>
public sealed class KnownGapFactAttribute : FactAttribute
{
    public KnownGapFactAttribute(string testCaseId, string gap)
    {
        if (Environment.GetEnvironmentVariable("DOCHECK_RUN_KNOWN_GAPS") != "1")
            Skip = $"Known gap {testCaseId}: {gap}";
    }
}

namespace DocumentChecker.Engine.Reading;

public static class DocumentReaderCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: DocumentChecker.Engine <path-to-document.docx>");
            return 2;
        }

        var result = new DocumentReader().Read(args[0]);
        if (!result.IsSuccess)
        {
            foreach (var issue in result.Issues)
                Console.Error.WriteLine($"{issue.Code}: {issue.Message}");

            return 1;
        }

        var document = result.Document!;
        Console.WriteLine($"File: {document.FileName}");
        Console.WriteLine($"Paragraphs: {document.Paragraphs.Count}");
        Console.WriteLine($"Runs: {document.Paragraphs.Sum(paragraph => paragraph.Runs.Count)}");
        return 0;
    }
}

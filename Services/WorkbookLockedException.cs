namespace HisaabKitaab.Services;

/// <summary>
/// A workbook couldn't be saved, almost always because it's open in Excel
/// or another program.
/// </summary>
public class WorkbookLockedException : IOException
{
    public WorkbookLockedException(string filePath, Exception inner)
        : base($"{Path.GetFileName(filePath)} is open in another program (probably Excel). Close it and try again.", inner)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}

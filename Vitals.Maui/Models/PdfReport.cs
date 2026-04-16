namespace Vitals.Maui.Models;

public class PdfReport
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public int Days { get; set; }

    public string DisplayName => FileName;
    public string DisplayDate => GeneratedAt.ToString("MMM d, yyyy h:mm tt");
    public string DisplayDays => $"{Days}-day report";
}
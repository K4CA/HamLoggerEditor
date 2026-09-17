namespace HamLogger.Models;

public class Contact
{
    public long Id { get; set; }
    public string Call { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTime QsoDate { get; set; }
    public TimeSpan TimeOn { get; set; }
    public decimal? Frequency { get; set; }
    public string RstSent { get; set; } = string.Empty;
    public string RstReceived { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Qth { get; set; } = string.Empty;
    public string GridSquare { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

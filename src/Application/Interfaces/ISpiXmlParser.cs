namespace ConvivenciaPix.Application.Interfaces;

public interface ISpiXmlParser
{
    string ExtractMessageId(string xml);
    string ExtractEndToEndId(string xml);
    decimal ExtractAmount(string xml);
    string ExtractPayerId(string xml);
    string ExtractPayeeId(string xml);
    DateTimeOffset ExtractTimestamp(string xml);
}

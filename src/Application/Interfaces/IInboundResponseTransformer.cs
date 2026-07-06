namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Rewrites an inbound Bacen response (e.g. pacs.002) so it carries the values System B
/// expects. System A and System B send independent pacs.008 requests whose fields differ
/// (e.g. EndToEndId, initiation form LclInstrm/Prtry). Bacen's response references System A's
/// values; this transformer maps them to System B's, driven by the stored pacs.008 A/B pair.
/// </summary>
public interface IInboundResponseTransformer
{
    /// <summary>
    /// Returns <paramref name="responseXml"/> with each configured field rewritten from
    /// System A's value to System B's value, computed from the two pacs.008 requests.
    /// Rules whose target node is absent in the response are skipped.
    /// </summary>
    string Transform(string responseXml, string systemAPacs008Xml, string systemBPacs008Xml);
}

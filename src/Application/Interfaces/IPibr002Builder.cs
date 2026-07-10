namespace ConvivenciaPix.Application.Interfaces;

/// <summary>
/// Builds a SPI Echo reply (pibr.002 / EchoRpt) from an incoming Echo request
/// (pibr.001 / EchoReq). Unlike <see cref="IInboundResponseTransformer"/>, which rewrites an
/// existing Bacen response, this synthesises a brand-new message: System B originates the
/// pibr.001 itself, so there is no System A response to mirror.
/// </summary>
public interface IPibr002Builder
{
    /// <summary>
    /// Returns the pibr.002 XML (unsigned) that answers <paramref name="pibr001Xml"/>:
    /// Fr/To are swapped, a fresh MsgId is minted, and the request's EchoTxInf/Data is echoed
    /// back as EchoTxInf/OrgnlData.
    /// </summary>
    string Build(string pibr001Xml);
}

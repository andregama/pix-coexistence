using System.Security.Cryptography;

namespace ConvivenciaPix.V2.Tests.Shared;

/// <summary>
/// SPI messages in the catalog's envelope shape (<c>Envelope</c> → <c>AppHdr</c> + <c>Document</c>),
/// with only the fields the proxy reads.
/// </summary>
public static class SpiXml
{
    public const string BankIspb = "12345678";
    public const string BacenIspb = "00038166";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string NewMsgId(string ispb = BankIspb) => $"M{ispb}{Random(23)}";

    public static string NewEndToEndId(string ispb = BankIspb) => $"E{ispb}202601011200{Random(11)}";

    public static string NewReturnId(string ispb = BankIspb) => $"D{ispb}202601011200{Random(11)}";

    public static string Envelope(
        string family, string version, string businessElement, string inner, string msgId,
        string from = BankIspb, string to = BacenIspb) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Envelope xmlns="https://www.bcb.gov.br/pi/{family}/{version}">
          <AppHdr>
            <Fr><FIId><FinInstnId><Othr><Id>{from}</Id></Othr></FinInstnId></FIId></Fr>
            <To><FIId><FinInstnId><Othr><Id>{to}</Id></Othr></FinInstnId></FIId></To>
            <BizMsgIdr>{msgId}</BizMsgIdr>
            <MsgDefIdr>{family}.spi.{version}</MsgDefIdr>
            <CreDt>2026-01-01T12:00:00.000Z</CreDt>
            <Sgntr/>
          </AppHdr>
          <Document>
            <{businessElement}>{inner}</{businessElement}>
          </Document>
        </Envelope>
        """;

    public static string Pacs008(
        string msgId, string endToEndId, string creditorDocument = "11122233344",
        string debtorDocument = "55566677788", string from = BankIspb, string to = BacenIspb, string extra = "") =>
        Envelope("pacs.008", "1.13", "FIToFICstmrCdtTrf",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:00.000Z</CreDtTm><NbOfTxs>1</NbOfTxs></GrpHdr>
            <CdtTrfTxInf>
              <PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId>
              <IntrBkSttlmAmt Ccy="BRL">10.00</IntrBkSttlmAmt>
              <Dbtr><Nm>Pagador</Nm><Id><PrvtId><Othr><Id>{debtorDocument}</Id></Othr></PrvtId></Id></Dbtr>
              <DbtrAcct><Id><Othr><Id>1234</Id><Issr>0001</Issr></Othr></Id></DbtrAcct>
              <Cdtr><Id><PrvtId><Othr><Id>{creditorDocument}</Id></Othr></PrvtId></Id></Cdtr>
              <CdtrAcct><Id><Othr><Id>5678</Id><Issr>0001</Issr></Othr></Id></CdtrAcct>
              {extra}
            </CdtTrfTxInf>
            """, msgId, from, to);

    /// <summary>
    /// Status report. As in the catalog, it names no original message: <paramref name="instructionId"/>
    /// is the EndToEndId of the payment or the RtrId of the return being reported.
    /// </summary>
    public static string Pacs002(
        string msgId, string instructionId, string status = "ACSP", string? originalEndToEndId = null,
        string from = BacenIspb, string to = BankIspb) =>
        Envelope("pacs.002", "1.17", "FIToFIPmtStsRpt",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:01.000Z</CreDtTm></GrpHdr>
            {Pacs002Transaction(instructionId, status, originalEndToEndId)}
            """, msgId, from, to);

    public static string Pacs002Transaction(string instructionId, string status = "ACSP", string? originalEndToEndId = null) =>
        $"""
        <TxInfAndSts>
          <OrgnlInstrId>{instructionId}</OrgnlInstrId>
          <OrgnlEndToEndId>{originalEndToEndId ?? instructionId}</OrgnlEndToEndId>
          <TxSts>{status}</TxSts>
        </TxInfAndSts>
        """;

    /// <summary>One pacs.008 transaction, for building messages that carry several.</summary>
    public static string Pacs008Transaction(string endToEndId, string creditorDocument) =>
        $"""
        <CdtTrfTxInf>
          <PmtId><EndToEndId>{endToEndId}</EndToEndId></PmtId>
          <IntrBkSttlmAmt Ccy="BRL">10.00</IntrBkSttlmAmt>
          <Dbtr><Nm>Pagador</Nm><Id><PrvtId><Othr><Id>55566677788</Id></Othr></PrvtId></Id></Dbtr>
          <Cdtr><Id><PrvtId><Othr><Id>{creditorDocument}</Id></Othr></PrvtId></Id></Cdtr>
        </CdtTrfTxInf>
        """;

    public static string Camt060(string msgId, string from = BankIspb, string to = BacenIspb) =>
        Envelope("camt.060", "1.9", "AcctRptgReq",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:00.000Z</CreDtTm></GrpHdr>
            <RptgReq><ReqdMsgNmId>camt.053</ReqdMsgNmId></RptgReq>
            """, msgId, from, to);

    /// <summary>Statement. <paramref name="statementId"/> is the camt.060 it answers, or zeros when unsolicited.</summary>
    public static string Camt053(string msgId, string statementId, string from = BacenIspb, string to = BankIspb) =>
        Envelope("camt.053", "1.4", "BkToCstmrStmt",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:01.000Z</CreDtTm></GrpHdr>
            <Stmt><Id>{statementId}</Id><Acct><Id><Othr><Id>{to}</Id></Othr></Id></Acct></Stmt>
            """, msgId, from, to);

    public static string Pacs004(
        string msgId, string returnId, string originalEndToEndId, string from = BacenIspb, string to = BankIspb) =>
        Envelope("pacs.004", "1.5", "PmtRtr",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:02.000Z</CreDtTm></GrpHdr>
            <TxInf>
              <RtrId>{returnId}</RtrId>
              <OrgnlEndToEndId>{originalEndToEndId}</OrgnlEndToEndId>
              <RtrdIntrBkSttlmAmt Ccy="BRL">10.00</RtrdIntrBkSttlmAmt>
            </TxInf>
            """, msgId, from, to);

    public static string Pibr001(string msgId, string data, string from = BankIspb, string to = BacenIspb) =>
        Envelope("pibr.001", "1.3", "EchoReq",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:00.000Z</CreDtTm></GrpHdr>
            <EchoTxInf><Data>{data}</Data></EchoTxInf>
            """, msgId, from, to);

    public static string Pibr002(string msgId, string originalData, string from = BacenIspb, string to = BankIspb) =>
        Envelope("pibr.002", "1.3", "EchoRpt",
            $"""
            <GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:00.500Z</CreDtTm></GrpHdr>
            <EchoTxInf><OrgnlData>{originalData}</OrgnlData></EchoTxInf>
            """, msgId, from, to);

    public static string Admi002(string msgId, string relatedReference, string from = BacenIspb, string to = BankIspb) =>
        Envelope("admi.002", "1.5", "MErr",
            $"""
            <RltdRef><Ref>{relatedReference}</Ref></RltdRef>
            <Rsn><RjctgPtyRsn>AM05</RjctgPtyRsn><RjctnDtTm>2026-01-01T12:00:00.000Z</RjctnDtTm></Rsn>
            """, msgId, from, to);

    public static string Admi004(string msgId, string from = BacenIspb, string to = BankIspb) =>
        Envelope("admi.004", "1.3", "SysEvtNtfctn",
            "<EvtInf><EvtCd>S001</EvtCd><EvtDesc>Abertura da grade</EvtDesc></EvtInf>", msgId, from, to);

    private static string Random(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}

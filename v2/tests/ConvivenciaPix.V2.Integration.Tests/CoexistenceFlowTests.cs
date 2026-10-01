using ConvivenciaPix.V2.BacenSpiSimulator;
using ConvivenciaPix.V2.SpiProxyApi.Listeners;
using ConvivenciaPix.V2.Tests.Shared;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ConvivenciaPix.V2.Integration.Tests;

[Collection(ProxyCollection.Name)]
public sealed class CoexistenceFlowTests(ProxyFixture fixture)
{
    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    private static string NewCpf() => Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

    /// <summary>
    /// What a system receives is what Bacen sent, under the bank's signature instead of Bacen's.
    /// </summary>
    private void ShouldBeSignedCopyOf(string delivered, string original)
    {
        fixture.Signer.Verify(Bytes(delivered)).Should().BeTrue("every delivered message carries the bank's signature");
        Unsigned(delivered).Should().Be(Unsigned(original));
    }

    private static string Unsigned(string xml) =>
        Regex.Replace(xml, "<Signature .*?</Signature>", string.Empty, RegexOptions.Singleline)
            .Replace("<Sgntr />", "<Sgntr/>").Replace("<Sgntr></Sgntr>", "<Sgntr/>");

    private static int Count(string xml, string element) => Regex.Matches(xml, $"<{element}>").Count;

    private async Task RegisterForSystemBAsync(string documento)
    {
        var response = await fixture.Admin.PostAsJsonAsync(
            "/admin/v1/system-b-documents", new { conta = "12345", agencia = "0001", documento });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- sending -----------------------------------------------------------------------------

    [Fact]
    public async Task EachSystem_ReceivesOnlyTheResponseToItsOwnMessage()
    {
        var (msgA, e2eA) = (SpiXml.NewMsgId(), SpiXml.NewEndToEndId());
        var (msgB, e2eB) = (SpiXml.NewMsgId(), SpiXml.NewEndToEndId());
        var systemA = fixture.SystemA;
        var systemB = fixture.SystemB;

        (await systemA.SendAsync(SpiXml.Pacs008(msgA, e2eA))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await systemB.SendAsync(SpiXml.Pacs008(msgB, e2eB))).StatusCode.Should().Be(HttpStatusCode.Created);

        // The SPI's pacs.002 names the payment, not the message that carried it.
        var replyA = await systemA.PullUntilAsync(xml => xml.Contains(e2eA));
        var replyB = await systemB.PullUntilAsync(xml => xml.Contains(e2eB));

        replyA.Should().Contain("pacs.002").And.NotContain(msgA);
        replyB.Should().Contain("pacs.002").And.NotContain(msgB);

        (await systemA.DrainAsync(2)).Should().NotContain(xml => xml.Contains(e2eB));
        (await systemB.DrainAsync(2)).Should().NotContain(xml => xml.Contains(e2eA));
    }

    [Fact]
    public async Task SentMessage_ReachesTheSpiByteForByte_AndItsResourceIdIsReturned()
    {
        var msgId = SpiXml.NewMsgId();
        var xml = SpiXml.Pacs008(msgId, SpiXml.NewEndToEndId()) + "\n<!-- trailing bytes: áéí -->";

        var response = await fixture.SystemB.SendAsync(xml);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var forwarded = fixture.Bacen.Received.Single(m => m.MsgId == msgId);
        forwarded.Body.Should().Equal(Bytes(xml));
        response.Headers.GetValues("PI-ResourceId").Single().Should().Be(forwarded.PiResourceId);

        (await fixture.ScalarAsync<string>($"SELECT SystemName FROM dbo.SpiSentRoute WHERE MsgId = '{msgId}'")).Should().Be("SystemB");
        (await fixture.ScalarAsync<string>($"SELECT PiResourceId FROM dbo.SpiSentRoute WHERE MsgId = '{msgId}'")).Should().Be(forwarded.PiResourceId);
    }

    [Fact]
    public async Task MultipartSend_ForwardsEveryMessage_AndRecordsEachRoute()
    {
        var first = SpiXml.NewMsgId();
        var second = SpiXml.NewMsgId();

        var response = await fixture.SystemA.SendMultipartAsync(
            SpiXml.Pacs008(first, SpiXml.NewEndToEndId()), SpiXml.Pacs008(second, SpiXml.NewEndToEndId()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("PI-ResourceId").Single().Split(',').Should().HaveCount(2);
        fixture.Bacen.Received.Should().Contain(m => m.MsgId == first).And.Contain(m => m.MsgId == second);
        (await fixture.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.SpiSentRoute WHERE MsgId IN ('{first}', '{second}') AND SystemName = 'SystemA' AND PiResourceId IS NOT NULL"))
            .Should().Be(2);
    }

    [Fact]
    public async Task SpiError_IsReturnedToTheSenderUnchanged()
    {
        var xml = SpiXml.Pacs008(SpiXml.NewMsgId(), SpiXml.NewEndToEndId(), extra: $"<!-- {SimulatorHost.HttpErrorMarker} -->");

        var response = await fixture.SystemB.SendAsync(xml);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+xml");
        (await response.Content.ReadAsStringAsync()).Should().Contain("<status>400</status>");
    }

    [Fact]
    public async Task RejectedPayment_ReachesItsSenderOnly()
    {
        var endToEndId = SpiXml.NewEndToEndId();
        var systemB = fixture.SystemB;

        await systemB.SendAsync(SpiXml.Pacs008(SpiXml.NewMsgId(), endToEndId, extra: $"<!-- {SimulatedReplies.RejectMarker} -->"));

        (await systemB.PullUntilAsync(xml => xml.Contains(endToEndId))).Should().Contain("<TxSts>RJCT</TxSts>");
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/xml", HttpStatusCode.BadRequest)]
    public async Task InvalidRequest_IsRefusedWithoutReachingTheSpi(string contentType, HttpStatusCode expected)
    {
        var before = fixture.Bacen.Received.Count;
        var content = new ByteArrayContent(contentType == "application/xml" ? [] : "{}"u8.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var response = await fixture.SystemA.Http.PostAsync($"/api/v1/in/{ProxyFixture.Ispb}/msgs", content);

        response.StatusCode.Should().Be(expected);
        fixture.Bacen.Received.Count.Should().Be(before);
    }

    // ---- receiving ---------------------------------------------------------------------------

    [Fact]
    public async Task ReceivedTransfer_ForACustomerOfSystemB_GoesToSystemBOnly()
    {
        var cpf = NewCpf();
        await RegisterForSystemBAsync($"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}");
        var endToEndId = SpiXml.NewEndToEndId("99999999");
        var xml = SpiXml.Pacs008(SpiXml.NewMsgId("99999999"), endToEndId, creditorDocument: cpf,
            from: SpiXml.BacenIspb, to: SpiXml.BankIspb);

        var resourceId = fixture.Bacen.Enqueue(Bytes(xml));

        (await fixture.WaitForRoutingAsync(resourceId)).Should().Be(("SystemB", "DocumentRegistry"));
        ShouldBeSignedCopyOf(await fixture.SystemB.PullUntilAsync(m => m.Contains(endToEndId)), xml);
        (await fixture.ScalarAsync<byte[]>($"SELECT Payload FROM dbo.SpiInboundMsg WHERE PiResourceId = '{resourceId}'"))
            .Should().Equal(Bytes(xml), "what Bacen sent is kept as received");
        (await fixture.SystemA.DrainAsync(2)).Should().NotContain(m => m.Contains(endToEndId));
    }

    [Fact]
    public async Task ReceivedTransfer_ForAnyOtherCustomer_GoesToSystemAOnly()
    {
        var endToEndId = SpiXml.NewEndToEndId("99999999");
        var xml = SpiXml.Pacs008(SpiXml.NewMsgId("99999999"), endToEndId, creditorDocument: NewCpf(),
            from: SpiXml.BacenIspb, to: SpiXml.BankIspb);

        var resourceId = fixture.Bacen.Enqueue(Bytes(xml));

        (await fixture.WaitForRoutingAsync(resourceId)).Should().Be(("SystemA", "DocumentRegistry"));
        ShouldBeSignedCopyOf(await fixture.SystemA.PullUntilAsync(m => m.Contains(endToEndId)), xml);
        (await fixture.SystemB.DrainAsync(2)).Should().NotContain(m => m.Contains(endToEndId));
    }

    [Fact]
    public async Task CustomerMovedBackToSystemA_MidTransaction_FollowUpStillGoesToSystemB()
    {
        var cpf = NewCpf();
        await RegisterForSystemBAsync(cpf);
        var endToEndId = SpiXml.NewEndToEndId("99999999");
        var received = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs008(
            SpiXml.NewMsgId("99999999"), endToEndId, creditorDocument: cpf, from: SpiXml.BacenIspb, to: SpiXml.BankIspb)));
        (await fixture.WaitForRoutingAsync(received)).Targets.Should().Be("SystemB");

        (await fixture.Admin.DeleteAsync($"/admin/v1/system-b-documents/{cpf}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Settlement confirmation for the same payment: it carries no document and names no
        // message — it must follow the transaction.
        var confirmation = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs002(
            SpiXml.NewMsgId(SpiXml.BacenIspb), endToEndId, "ACCC")));

        (await fixture.WaitForRoutingAsync(confirmation)).Should().Be(("SystemB", "TxAffinity"));
    }

    [Fact]
    public async Task ReturnOfAPaymentSentBySystemB_GoesToSystemB()
    {
        var endToEndId = SpiXml.NewEndToEndId();
        var systemB = fixture.SystemB;
        await systemB.SendAsync(SpiXml.Pacs008(SpiXml.NewMsgId(), endToEndId));

        var returned = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs004(
            SpiXml.NewMsgId("99999999"), SpiXml.NewReturnId("99999999"), endToEndId)));

        (await fixture.WaitForRoutingAsync(returned)).Should().Be(("SystemB", "TxAffinity"));
    }

    [Fact]
    public async Task StatusOfAReturnSentBySystemB_GoesToSystemB()
    {
        // System B returns a payment; the SPI's pacs.002 identifies the return by its RtrId.
        var returnId = SpiXml.NewReturnId();
        var originalEndToEndId = SpiXml.NewEndToEndId("99999999");
        await fixture.SystemB.SendAsync(SpiXml.Pacs004(
            SpiXml.NewMsgId(), returnId, originalEndToEndId, from: SpiXml.BankIspb, to: SpiXml.BacenIspb));

        var status = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs002(
            SpiXml.NewMsgId(SpiXml.BacenIspb), returnId, "ACCC", originalEndToEndId)));

        (await fixture.WaitForRoutingAsync(status)).Should().Be(("SystemB", "TxAffinity"));
    }

    [Fact]
    public async Task StatusReportCoveringPaymentsOfBothSystems_IsSplit_EachSystemSeesOnlyItsOwnPayment()
    {
        // The SPI may report several payments in one pacs.002.
        var (e2eA, e2eB) = (SpiXml.NewEndToEndId(), SpiXml.NewEndToEndId());
        var systemA = fixture.SystemA;
        var systemB = fixture.SystemB;
        await systemA.SendAsync(SpiXml.Pacs008(SpiXml.NewMsgId(), e2eA));
        await systemB.SendAsync(SpiXml.Pacs008(SpiXml.NewMsgId(), e2eB));

        var msgId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var report = fixture.Bacen.Enqueue(Bytes(SpiXml.Envelope("pacs.002", "1.17", "FIToFIPmtStsRpt",
            SpiXml.Pacs002Transaction(e2eA, "ACCC") + SpiXml.Pacs002Transaction(e2eB, "ACCC"),
            msgId, SpiXml.BacenIspb, SpiXml.BankIspb)));

        (await fixture.WaitForRoutingAsync(report)).Should().Be(("SystemA,SystemB", "Split"));

        var forA = await systemA.PullUntilAsync(m => m.Contains(msgId));
        var forB = await systemB.PullUntilAsync(m => m.Contains(msgId));
        forA.Should().Contain(e2eA).And.NotContain(e2eB);
        forB.Should().Contain(e2eB).And.NotContain(e2eA);
        fixture.Signer.Verify(Bytes(forA)).Should().BeTrue();
        fixture.Signer.Verify(Bytes(forB)).Should().BeTrue();
    }

    [Fact]
    public async Task ReceivedTransfersForCustomersOfBothSystemsInOneMessage_AreSplit_EachSystemGetsItsCustomersTransfers()
    {
        var cpfB = NewCpf();
        await RegisterForSystemBAsync(cpfB);
        var (e2eA1, e2eB, e2eA2) = (SpiXml.NewEndToEndId("99999999"), SpiXml.NewEndToEndId("99999999"), SpiXml.NewEndToEndId("99999999"));
        var msgId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var original = SpiXml.Envelope("pacs.008", "1.16", "FIToFICstmrCdtTrf",
            $"<GrpHdr><MsgId>{msgId}</MsgId><CreDtTm>2026-01-01T12:00:00.000Z</CreDtTm><NbOfTxs>3</NbOfTxs></GrpHdr>"
            + SpiXml.Pacs008Transaction(e2eA1, NewCpf())
            + SpiXml.Pacs008Transaction(e2eB, cpfB)
            + SpiXml.Pacs008Transaction(e2eA2, NewCpf()),
            msgId, SpiXml.BacenIspb, SpiXml.BankIspb);

        var transfers = fixture.Bacen.Enqueue(Bytes(original));
        (await fixture.WaitForRoutingAsync(transfers)).Should().Be(("SystemA,SystemB", "Split"));

        var forA = await fixture.SystemA.PullUntilAsync(m => m.Contains(msgId));
        var forB = await fixture.SystemB.PullUntilAsync(m => m.Contains(msgId));

        // Each system receives a complete, signed message holding only its own customers' transfers.
        forA.Should().Contain(e2eA1).And.Contain(e2eA2).And.NotContain(e2eB).And.NotContain(cpfB);
        forA.Should().Contain("<NbOfTxs>2</NbOfTxs>");
        Count(forA, "CdtTrfTxInf").Should().Be(2);
        forB.Should().Contain(e2eB).And.NotContain(e2eA1).And.NotContain(e2eA2);
        forB.Should().Contain("<NbOfTxs>1</NbOfTxs>");
        Count(forB, "CdtTrfTxInf").Should().Be(1);
        fixture.Signer.Verify(Bytes(forA)).Should().BeTrue();
        fixture.Signer.Verify(Bytes(forB)).Should().BeTrue();

        // What Bacen sent is kept whole.
        (await fixture.ScalarAsync<byte[]>($"SELECT Payload FROM dbo.SpiInboundMsg WHERE PiResourceId = '{transfers}'"))
            .Should().Equal(Bytes(original));

        // And each payment's follow-up goes to the system that received it.
        var settledB = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs002(SpiXml.NewMsgId(SpiXml.BacenIspb), e2eB, "ACCC")));
        var settledA = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs002(SpiXml.NewMsgId(SpiXml.BacenIspb), e2eA2, "ACCC")));
        (await fixture.WaitForRoutingAsync(settledB)).Should().Be(("SystemB", "TxAffinity"));
        (await fixture.WaitForRoutingAsync(settledA)).Should().Be(("SystemA", "TxAffinity"));
    }

    [Fact]
    public async Task SeveralReceivedTransfersForCustomersOfSystemB_GoToSystemB()
    {
        var (cpf1, cpf2) = (NewCpf(), NewCpf());
        await RegisterForSystemBAsync(cpf1);
        await RegisterForSystemBAsync(cpf2);

        var transfers = fixture.Bacen.Enqueue(Bytes(SpiXml.Envelope("pacs.008", "1.16", "FIToFICstmrCdtTrf",
            SpiXml.Pacs008Transaction(SpiXml.NewEndToEndId("99999999"), cpf1)
            + SpiXml.Pacs008Transaction(SpiXml.NewEndToEndId("99999999"), cpf2),
            SpiXml.NewMsgId(SpiXml.BacenIspb), SpiXml.BacenIspb, SpiXml.BankIspb)));

        (await fixture.WaitForRoutingAsync(transfers)).Should().Be(("SystemB", "DocumentRegistry"));
    }

    [Fact]
    public async Task AccountReport_GoesToTheSystemThatAskedForIt_AndUnsolicitedOnesToBoth()
    {
        var request = SpiXml.NewMsgId();
        await fixture.SystemB.SendAsync(SpiXml.Camt060(request));

        var answer = fixture.Bacen.Enqueue(Bytes(SpiXml.Camt053(SpiXml.NewMsgId(SpiXml.BacenIspb), request)));
        var notice = fixture.Bacen.Enqueue(Bytes(SpiXml.Camt053(SpiXml.NewMsgId(SpiXml.BacenIspb), new string('0', 32))));

        (await fixture.WaitForRoutingAsync(answer)).Should().Be(("SystemB", "SentByOriginalMsgId"));
        (await fixture.WaitForRoutingAsync(notice)).Should().Be(("SystemA,SystemB", "Broadcast"));
    }

    [Fact]
    public async Task BacenNotification_GoesToBothSystems()
    {
        var msgId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var xml = SpiXml.Admi004(msgId);

        var resourceId = fixture.Bacen.Enqueue(Bytes(xml));

        (await fixture.WaitForRoutingAsync(resourceId)).Should().Be(("SystemA,SystemB", "Broadcast"));
        ShouldBeSignedCopyOf(await fixture.SystemA.PullUntilAsync(m => m.Contains(msgId)), xml);
        ShouldBeSignedCopyOf(await fixture.SystemB.PullUntilAsync(m => m.Contains(msgId)), xml);
    }

    [Fact]
    public async Task EchoReply_GoesToTheSystemThatSentThatContent()
    {
        var dataA = "echo-" + Guid.NewGuid().ToString("N");
        var dataB = "echo-" + Guid.NewGuid().ToString("N");
        var systemA = fixture.SystemA;
        var systemB = fixture.SystemB;

        await systemA.SendAsync(SpiXml.Pibr001(SpiXml.NewMsgId(), dataA));
        await systemB.SendAsync(SpiXml.Pibr001(SpiXml.NewMsgId(), dataB));

        (await systemA.PullUntilAsync(m => m.Contains(dataA))).Should().Contain("pibr.002");
        (await systemB.PullUntilAsync(m => m.Contains(dataB))).Should().Contain("pibr.002");
        (await systemA.DrainAsync(2)).Should().NotContain(m => m.Contains(dataB));
    }

    [Fact]
    public async Task IdenticalEchoFromBothSystems_EachSystemGetsExactlyOneReply()
    {
        var data = "same-" + Guid.NewGuid().ToString("N");
        var systemA = fixture.SystemA;
        var systemB = fixture.SystemB;

        await systemA.SendAsync(SpiXml.Pibr001(SpiXml.NewMsgId(), data));
        await systemB.SendAsync(SpiXml.Pibr001(SpiXml.NewMsgId(), data));

        await systemA.PullUntilAsync(m => m.Contains(data));
        await systemB.PullUntilAsync(m => m.Contains(data));
        (await systemA.DrainAsync(2)).Should().NotContain(m => m.Contains(data));
        (await systemB.DrainAsync(2)).Should().NotContain(m => m.Contains(data));
    }

    [Fact]
    public async Task ResponseToAMessageTheProxyNeverSaw_GoesToSystemA()
    {
        // Traffic System A started before the proxy took over the connection.
        var resourceId = fixture.Bacen.Enqueue(Bytes(SpiXml.Pacs002(
            SpiXml.NewMsgId(SpiXml.BacenIspb), SpiXml.NewEndToEndId())));

        (await fixture.WaitForRoutingAsync(resourceId)).Should().Be(("SystemA", "Default"));
    }

    [Fact]
    public async Task RejectionOfASentMessage_GoesBackToItsSender()
    {
        var msgId = SpiXml.NewMsgId();
        await fixture.SystemB.SendAsync(SpiXml.Pibr001(msgId, "x-" + Guid.NewGuid().ToString("N")));

        var rejection = fixture.Bacen.Enqueue(Bytes(SpiXml.Admi002(SpiXml.NewMsgId(SpiXml.BacenIspb), msgId)));

        (await fixture.WaitForRoutingAsync(rejection)).Should().Be(("SystemB", "SentByOriginalMsgId"));
    }

    [Fact]
    public async Task UnreadableMessage_IsQuarantined_AndTheStreamMovesOn()
    {
        var garbage = "not xml " + Guid.NewGuid().ToString("N");

        var resourceId = fixture.Bacen.Enqueue(Bytes(garbage));
        var after = fixture.Bacen.Enqueue(Bytes(SpiXml.Admi004(SpiXml.NewMsgId(SpiXml.BacenIspb))));

        (await fixture.WaitForRoutingAsync(resourceId)).Should().Be(("", "Unreadable"));
        (await fixture.WaitForRoutingAsync(after)).Targets.Should().Be("SystemA,SystemB");
        (await fixture.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}'")).Should().Be(0);
    }

    [Fact]
    public async Task WithVerificationOn_OnlyMessagesWhoseSignatureVerifiesAreDelivered()
    {
        // In this fixture the mock HSM is the only signer, so it also plays Bacen here.
        var genuineId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var genuine = fixture.Signer.Sign(Bytes(SpiXml.Admi004(genuineId)));
        var tampered = Bytes(Encoding.UTF8.GetString(fixture.Signer.Sign(Bytes(SpiXml.Admi004(SpiXml.NewMsgId(SpiXml.BacenIspb)))))
            .Replace("Abertura da grade", "Conteúdo alterado"));
        var unsigned = Bytes(SpiXml.Admi004(SpiXml.NewMsgId(SpiXml.BacenIspb)));

        fixture.InboundSigning.VerifyBacenSignature = true;
        try
        {
            var tamperedId = fixture.Bacen.Enqueue(tampered);
            var unsignedId = fixture.Bacen.Enqueue(unsigned);
            var genuineResource = fixture.Bacen.Enqueue(genuine);

            (await fixture.WaitForRoutingAsync(tamperedId)).Should().Be(("", "InvalidSignature"));
            (await fixture.WaitForRoutingAsync(unsignedId)).Should().Be(("", "InvalidSignature"));
            (await fixture.WaitForRoutingAsync(genuineResource)).Should().Be(("SystemA,SystemB", "Broadcast"));

            (await fixture.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId IN ('{tamperedId}', '{unsignedId}')")).Should().Be(0);
            var delivered = await fixture.SystemA.PullUntilAsync(m => m.Contains(genuineId));
            Count(delivered, "SignatureValue").Should().Be(1, "Bacen's signature is replaced, not kept next to the bank's");
        }
        finally
        {
            fixture.InboundSigning.VerifyBacenSignature = false;
        }
    }

    [Fact]
    public async Task MessageRedeliveredByTheSpi_IsDeliveredToTheSystemOnce()
    {
        var msgId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var xml = Bytes(SpiXml.Admi004(msgId));
        var resourceId = fixture.Bacen.Enqueue(xml);
        await fixture.WaitForRoutingAsync(resourceId);

        fixture.Bacen.Enqueue(xml, resourceId);
        fixture.Bacen.Enqueue(xml, resourceId);

        var delivered = await fixture.SystemA.DrainAsync(4);
        delivered.Count(m => m.Contains(msgId)).Should().Be(1);
        (await fixture.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}' AND SystemName = 'SystemA'")).Should().Be(1);
    }

    // ---- pull stream semantics ---------------------------------------------------------------

    [Fact]
    public async Task BatchNeverAcknowledged_IsDeliveredAgainAfterItsLease()
    {
        var msgId = SpiXml.NewMsgId(SpiXml.BacenIspb);
        var resourceId = fixture.Bacen.Enqueue(Bytes(SpiXml.Admi004(msgId)));
        await fixture.WaitForRoutingAsync(resourceId);

        // First consumer receives it and dies without following PI-Pull-Next.
        var crashed = fixture.SystemB;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var seen = false;
        while (!seen && DateTime.UtcNow < deadline)
        {
            var batch = await crashed.PullOnceAsync(advance: false);
            seen = batch.Any(m => m.PiResourceId == resourceId);
            if (!seen && batch.Count > 0)
                await fixture.ExecuteSqlAsync(
                    "UPDATE dbo.SpiInboundDelivery SET AckedAt = SYSUTCDATETIME() WHERE SystemName = 'SystemB' AND LeaseStreamId IS NOT NULL AND AckedAt IS NULL AND PiResourceId <> '" + resourceId + "'");
        }
        seen.Should().BeTrue();

        // A new consumer gets it again once the lease (3s in this fixture) runs out.
        (await fixture.SystemB.PullUntilAsync(m => m.Contains(msgId))).Should().Contain(msgId);
        (await fixture.ScalarAsync<int>(
            $"SELECT DeliveryCount FROM dbo.SpiInboundDelivery WHERE PiResourceId = '{resourceId}' AND SystemName = 'SystemB'")).Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task EmptyStream_AnswersNoContentWithTheNextUrl_AndDeleteClosesIt()
    {
        var http = fixture.SystemA.Http;
        HttpResponseMessage response;
        do
        {
            response = await http.GetAsync($"/api/v1/out/{ProxyFixture.Ispb}/stream/start");
        } while (response.StatusCode == HttpStatusCode.OK); // drain leftovers from other tests

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var next = response.Headers.GetValues("PI-Pull-Next").Single();
        next.Should().MatchRegex($"^/api/v1/out/{ProxyFixture.Ispb}/stream/[0-9a-f]{{12}}$");

        (await http.DeleteAsync(next)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- listeners ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ListenerKind.Admin, "/api/v1/out/12345678/stream/start")]
    [InlineData(null, "/api/v1/out/12345678/stream/start")]
    [InlineData(ListenerKind.SystemA, "/admin/v1/system-b-documents")]
    [InlineData(ListenerKind.SystemB, "/admin/v1/system-b-documents")]
    [InlineData(ListenerKind.SystemB, "/swagger/v1/swagger.json")]
    [InlineData(ListenerKind.SystemA, "/metrics")]
    public async Task EndpointOnTheWrongListener_DoesNotExist(ListenerKind? listener, string path) =>
        (await fixture.ClientFor(listener).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task AnotherBanksIspb_IsRefused() =>
        (await fixture.SystemA.Http.GetAsync("/api/v1/out/87654321/stream/start")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // ---- administration ----------------------------------------------------------------------

    [Fact]
    public async Task AdminApi_ListsAddsAndRemovesDocuments()
    {
        var cpf = NewCpf();
        var admin = fixture.Admin;

        var bulk = await admin.PostAsJsonAsync("/admin/v1/system-b-documents/bulk", new[]
        {
            new { conta = "1", agencia = "0001", documento = cpf },
            new { conta = "2", agencia = "0001", documento = cpf }
        });
        bulk.StatusCode.Should().Be(HttpStatusCode.Created);

        var listed = await admin.GetFromJsonAsync<List<DocumentRow>>($"/admin/v1/system-b-documents?documento={cpf}");
        listed.Should().HaveCount(2).And.OnlyContain(row => row.Documento == cpf);

        var invalid = await admin.PostAsJsonAsync("/admin/v1/system-b-documents", new { conta = "", agencia = "0001", documento = cpf });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.DeleteAsync($"/admin/v1/system-b-documents/{cpf}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/admin/v1/system-b-documents/{cpf}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record DocumentRow(int Id, string Conta, string Agencia, string Documento, DateTime DataCriacao);
}

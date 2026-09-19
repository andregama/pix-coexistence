SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 11: pacs.002 transaction status (TxSts)
-- Adds TxStatus to both message tables so the analytics "received success" rule (outbound ack
-- accepted + inbound pacs.002 not rejected) can be evaluated on columns instead of parsing XML.
-- Idempotent: each ADD guards with IF COL_LENGTH(...) IS NULL.

IF COL_LENGTH('dbo.SpiSentMsg', 'TxStatus') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [TxStatus] VARCHAR(10) NULL;
GO

IF COL_LENGTH('dbo.SpiReceivedMsg', 'TxStatus') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [TxStatus] VARCHAR(10) NULL;
GO

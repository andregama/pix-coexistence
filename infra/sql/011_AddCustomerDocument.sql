SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 13: bank customer's CPF/CNPJ
-- Adds CustomerDocument (CPF 11 / CNPJ 14 chars, punctuation removed) to both message tables.
-- Filled on insert/update by the correlate worker; existing rows are backfilled by
-- tools/CustomerDocumentBackfill.
-- Idempotent: each ADD guards with IF COL_LENGTH(...) IS NULL.

IF COL_LENGTH('dbo.SpiSentMsg', 'CustomerDocument') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [CustomerDocument] VARCHAR(14) NULL;
GO

IF COL_LENGTH('dbo.SpiReceivedMsg', 'CustomerDocument') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [CustomerDocument] VARCHAR(14) NULL;
GO

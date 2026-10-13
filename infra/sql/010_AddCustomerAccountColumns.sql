SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 12: customer account columns
-- Adds CustomerBranch / CustomerAccount / Ispb (the bank's customer in the message: branch, account
-- number and the ISPB of the participant holding the account) to both message tables.
-- Idempotent: each ADD guards with IF COL_LENGTH(...) IS NULL.

-- ============================================================
-- SpiSentMsg (outbound: PSP -> SPI)
-- ============================================================
IF COL_LENGTH('dbo.SpiSentMsg', 'CustomerBranch') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [CustomerBranch] VARCHAR(4) NULL;
GO

IF COL_LENGTH('dbo.SpiSentMsg', 'CustomerAccount') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [CustomerAccount] VARCHAR(20) NULL;
GO

IF COL_LENGTH('dbo.SpiSentMsg', 'Ispb') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [Ispb] VARCHAR(8) NULL;
GO

-- ============================================================
-- SpiReceivedMsg (inbound: SPI -> PSP)
-- ============================================================
IF COL_LENGTH('dbo.SpiReceivedMsg', 'CustomerBranch') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [CustomerBranch] VARCHAR(4) NULL;
GO

IF COL_LENGTH('dbo.SpiReceivedMsg', 'CustomerAccount') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [CustomerAccount] VARCHAR(20) NULL;
GO

IF COL_LENGTH('dbo.SpiReceivedMsg', 'Ispb') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [Ispb] VARCHAR(8) NULL;
GO

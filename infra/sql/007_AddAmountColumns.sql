SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 9: Transfer & withdrawal amounts
-- Adds TransferAmount / WithdrawalAmount (Pix transfer/troco vs Saque portions) to the
-- coexistence message tables so monetary value is queryable for the analytics dashboard.
-- Idempotent: each ADD guards with IF COL_LENGTH(...) IS NULL.

-- ============================================================
-- SpiSentMsg (outbound: PSP -> SPI)
-- ============================================================
IF COL_LENGTH('dbo.SpiSentMsg', 'TransferAmount') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [TransferAmount] DECIMAL(18,2) NULL;
GO

IF COL_LENGTH('dbo.SpiSentMsg', 'WithdrawalAmount') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [WithdrawalAmount] DECIMAL(18,2) NULL;
GO

-- ============================================================
-- SpiReceivedMsg (inbound: SPI -> PSP)
-- ============================================================
IF COL_LENGTH('dbo.SpiReceivedMsg', 'TransferAmount') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [TransferAmount] DECIMAL(18,2) NULL;
GO

IF COL_LENGTH('dbo.SpiReceivedMsg', 'WithdrawalAmount') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [WithdrawalAmount] DECIMAL(18,2) NULL;
GO

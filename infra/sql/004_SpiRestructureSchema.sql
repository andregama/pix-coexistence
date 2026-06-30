SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 8: Schema Restructure
-- Drops SpiPendingSystemBMsg (superseded) and recreates SpiSentMsg with the new
-- dual-table correlation schema. Creates SpiReceivedMsg for inbound messages.
-- Updates SpiDiscrepancies to use IdempotentId + MsgType instead of old system IDs.
-- Idempotent: all statements guard with IF EXISTS / IF NOT EXISTS.

-- ============================================================
-- 1. Drop SpiPendingSystemBMsg (replaced by SpiReceivedMsg)
-- ============================================================
IF OBJECT_ID('dbo.SpiPendingSystemBMsg', 'U') IS NOT NULL
    DROP TABLE [dbo].[SpiPendingSystemBMsg];
GO

-- ============================================================
-- 2. Drop old SpiSentMsg (schema completely replaced)
-- ============================================================
IF OBJECT_ID('dbo.SpiSentMsg', 'U') IS NOT NULL
    DROP TABLE [dbo].[SpiSentMsg];
GO

-- ============================================================
-- 3. Create new SpiSentMsg  (outbound: PSP → SPI)
-- Orchestrator pre-inserts a row before either system sends its XML.
-- ============================================================
CREATE TABLE [dbo].[SpiSentMsg] (
    [IdempotentId]            VARCHAR(255)   NOT NULL,  -- EndToEndId (pacs.008/pacs.002) or RtrId (pacs.004)
    [MsgType]                 VARCHAR(20)    NOT NULL,  -- e.g. 'pacs.008', 'pacs.004', 'pacs.002'
    [MsgIdSystemA]            VARCHAR(255)   NULL,
    [MsgIdSystemB]            VARCHAR(255)   NULL,
    [XmlMsgSystemA]           NVARCHAR(MAX)  NULL,
    [XmlMsgSystemB]           NVARCHAR(MAX)  NULL,
    [OriginalMsgIdempotentId] VARCHAR(255)   NULL,      -- pacs.004: OrgnlEndToEndId of original pacs.008
    [SystemAErrorCode]        VARCHAR(MAX)   NULL,
    [SystemBErrorCode]        VARCHAR(MAX)   NULL,
    [CreatedAt]               DATETIME2      NOT NULL,
    [UpdatedAt]               DATETIME2      NULL,
    CONSTRAINT [PK_SpiSentMsg] PRIMARY KEY CLUSTERED ([IdempotentId] ASC)
);
GO

CREATE NONCLUSTERED INDEX [IX_SpiSentMsg_MsgIdSystemA]
    ON [dbo].[SpiSentMsg] ([MsgIdSystemA] ASC)
    WHERE [MsgIdSystemA] IS NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_SpiSentMsg_MsgIdSystemB]
    ON [dbo].[SpiSentMsg] ([MsgIdSystemB] ASC)
    WHERE [MsgIdSystemB] IS NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_SpiSentMsg_OriginalMsgIdempotentId]
    ON [dbo].[SpiSentMsg] ([OriginalMsgIdempotentId] ASC)
    WHERE [OriginalMsgIdempotentId] IS NOT NULL;
GO

-- ============================================================
-- 4. Create SpiReceivedMsg  (inbound: SPI → PSP)
-- First arrival (System A CDC) creates the row; SpiProxyWorker
-- fills XmlMsgSystemB after signing and delivering to System B.
-- ============================================================
CREATE TABLE [dbo].[SpiReceivedMsg] (
    [IdempotentId]            VARCHAR(255)   NOT NULL,  -- EndToEndId (pacs.008) or RtrId (pacs.004) or OrgnlEndToEndId (pacs.002)
    [MsgType]                 VARCHAR(20)    NOT NULL,
    [MsgId]                   VARCHAR(255)   NULL,      -- shared <MsgId> from Bacen message; first-wins, not overwritten
    [XmlMsgSystemA]           NVARCHAR(MAX)  NULL,
    [XmlMsgSystemB]           NVARCHAR(MAX)  NULL,      -- signed XML delivered to System B by SpiProxyWorker
    [OriginalMsgIdempotentId] VARCHAR(255)   NULL,
    [SystemAErrorCode]        VARCHAR(MAX)   NULL,
    [SystemBErrorCode]        VARCHAR(MAX)   NULL,
    [CreatedAt]               DATETIME2      NOT NULL,
    [UpdatedAt]               DATETIME2      NULL,
    CONSTRAINT [PK_SpiReceivedMsg] PRIMARY KEY CLUSTERED ([IdempotentId] ASC)
);
GO

CREATE NONCLUSTERED INDEX [IX_SpiReceivedMsg_MsgId]
    ON [dbo].[SpiReceivedMsg] ([MsgId] ASC)
    WHERE [MsgId] IS NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_SpiReceivedMsg_OriginalMsgIdempotentId]
    ON [dbo].[SpiReceivedMsg] ([OriginalMsgIdempotentId] ASC)
    WHERE [OriginalMsgIdempotentId] IS NOT NULL;
GO

-- ============================================================
-- 5. Update SpiDiscrepancies
-- Replace old IdSystemA / IdSystemB / CorrelationSource with
-- IdempotentId + MsgType to align with the new schema.
-- ============================================================
IF OBJECT_ID('dbo.SpiDiscrepancies', 'U') IS NOT NULL
BEGIN
    -- Drop old index before altering columns
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiDiscrepancies_IdSystemA_DetectedAt'
               AND object_id = OBJECT_ID('dbo.SpiDiscrepancies'))
        DROP INDEX [IX_SpiDiscrepancies_IdSystemA_DetectedAt] ON [dbo].[SpiDiscrepancies];

    IF COL_LENGTH('dbo.SpiDiscrepancies', 'IdSystemA') IS NOT NULL
        ALTER TABLE [dbo].[SpiDiscrepancies] DROP COLUMN [IdSystemA];

    IF COL_LENGTH('dbo.SpiDiscrepancies', 'IdSystemB') IS NOT NULL
        ALTER TABLE [dbo].[SpiDiscrepancies] DROP COLUMN [IdSystemB];

    IF COL_LENGTH('dbo.SpiDiscrepancies', 'CorrelationSource') IS NOT NULL
        ALTER TABLE [dbo].[SpiDiscrepancies] DROP COLUMN [CorrelationSource];

    IF COL_LENGTH('dbo.SpiDiscrepancies', 'IdempotentId') IS NULL
        ALTER TABLE [dbo].[SpiDiscrepancies] ADD [IdempotentId] NVARCHAR(64) NOT NULL DEFAULT '';

    IF COL_LENGTH('dbo.SpiDiscrepancies', 'MsgType') IS NULL
        ALTER TABLE [dbo].[SpiDiscrepancies] ADD [MsgType] NVARCHAR(20) NOT NULL DEFAULT '';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiDiscrepancies_IdempotentId_DetectedAt'
                   AND object_id = OBJECT_ID('dbo.SpiDiscrepancies'))
        CREATE NONCLUSTERED INDEX [IX_SpiDiscrepancies_IdempotentId_DetectedAt]
            ON [dbo].[SpiDiscrepancies] ([IdempotentId] ASC, [DetectedAt] ASC);
END;
GO

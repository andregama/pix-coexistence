-- Table: SpiSentMsg
-- Stores the System A <-> System B identifier mapping produced by spi-correlate-worker.
-- Composite PK = (IdSystemA, IdSystemB); unique single-column indexes back the
-- FindByIdSystemAAsync / FindByIdSystemBAsync lookup patterns.

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'SpiSentMsg' AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE [dbo].[SpiSentMsg] (
        [IdSystemA]         VARCHAR(255) NOT NULL,
        [IdSystemB]         VARCHAR(255) NOT NULL,
        [CorrelationSource] VARCHAR(20)  NOT NULL,
        [CreatedAt]         DATETIME2    NOT NULL,
        CONSTRAINT [PK_SpiSentMsg] PRIMARY KEY CLUSTERED ([IdSystemA] ASC, [IdSystemB] ASC)
    );
END;
GO

-- These indexes are only valid for the pre-Phase-8 schema (IdSystemA/IdSystemB columns).
-- 004_SpiRestructureSchema.sql drops and recreates SpiSentMsg with IdempotentId as PK,
-- so we guard index creation with a column-existence check to stay idempotent.
IF COL_LENGTH('dbo.SpiSentMsg', 'IdSystemA') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes
       WHERE name = 'IX_SpiSentMsg_IdSystemA' AND object_id = OBJECT_ID('dbo.SpiSentMsg')
   )
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_SpiSentMsg_IdSystemA]
        ON [dbo].[SpiSentMsg] ([IdSystemA] ASC);
END;
GO

IF COL_LENGTH('dbo.SpiSentMsg', 'IdSystemB') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes
       WHERE name = 'IX_SpiSentMsg_IdSystemB' AND object_id = OBJECT_ID('dbo.SpiSentMsg')
   )
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_SpiSentMsg_IdSystemB]
        ON [dbo].[SpiSentMsg] ([IdSystemB] ASC);
END;
GO

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

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SpiSentMsg_IdSystemA' AND object_id = OBJECT_ID('dbo.SpiSentMsg')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_SpiSentMsg_IdSystemA]
        ON [dbo].[SpiSentMsg] ([IdSystemA] ASC);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SpiSentMsg_IdSystemB' AND object_id = OBJECT_ID('dbo.SpiSentMsg')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_SpiSentMsg_IdSystemB]
        ON [dbo].[SpiSentMsg] ([IdSystemB] ASC);
END;
GO

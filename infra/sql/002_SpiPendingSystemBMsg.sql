-- Table: SpiPendingSystemBMsg
-- Holds System B SPI requests that are waiting to be correlated with a System A
-- message. The (Timestamp, Amount) composite index supports the heuristic match.

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'SpiPendingSystemBMsg' AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE [dbo].[SpiPendingSystemBMsg] (
        [Id]           UNIQUEIDENTIFIER NOT NULL,
        [IdSystemB]    VARCHAR(255)     NOT NULL,
        [MessageId]    VARCHAR(255)     NOT NULL,
        [Timestamp]    DATETIMEOFFSET   NOT NULL,
        [Amount]       DECIMAL(18, 2)   NOT NULL,
        [PayerId]      VARCHAR(255)     NOT NULL,
        [PayeeId]      VARCHAR(255)     NOT NULL,
        [RawXmlBase64] NVARCHAR(MAX)    NOT NULL,
        [CreatedAt]    DATETIME2        NOT NULL,
        CONSTRAINT [PK_SpiPendingSystemBMsg] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SpiPendingSystemBMsg_IdSystemB' AND object_id = OBJECT_ID('dbo.SpiPendingSystemBMsg')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_SpiPendingSystemBMsg_IdSystemB]
        ON [dbo].[SpiPendingSystemBMsg] ([IdSystemB] ASC);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SpiPendingSystemBMsg_Timestamp_Amount' AND object_id = OBJECT_ID('dbo.SpiPendingSystemBMsg')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SpiPendingSystemBMsg_Timestamp_Amount]
        ON [dbo].[SpiPendingSystemBMsg] ([Timestamp] ASC, [Amount] ASC);
END;
GO

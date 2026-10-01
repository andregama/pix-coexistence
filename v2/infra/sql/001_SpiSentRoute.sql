-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- One row per message a system sent through the proxy. Written before the message is forwarded,
-- so a response from Bacen can always be routed back to its sender.
-- Identifier columns use a binary collation: SPI ids are case-sensitive.
IF OBJECT_ID('dbo.SpiSentRoute', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpiSentRoute
    (
        MsgId          VARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
        SystemName     VARCHAR(10)  NOT NULL,
        MsgType        VARCHAR(20)  NOT NULL,
        TxKey          VARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
        EchoDataHash   CHAR(64)     NULL,
        EchoAnsweredAt DATETIME2    NULL,
        PiResourceId   VARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
        HttpStatus     INT          NULL,
        CreatedAt      DATETIME2    NOT NULL CONSTRAINT DF_SpiSentRoute_CreatedAt DEFAULT SYSUTCDATETIME(),
        ForwardedAt    DATETIME2    NULL,
        CONSTRAINT PK_SpiSentRoute PRIMARY KEY CLUSTERED (MsgId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiSentRoute_PiResourceId' AND object_id = OBJECT_ID('dbo.SpiSentRoute'))
    CREATE INDEX IX_SpiSentRoute_PiResourceId ON dbo.SpiSentRoute (PiResourceId) WHERE PiResourceId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiSentRoute_Echo' AND object_id = OBJECT_ID('dbo.SpiSentRoute'))
    CREATE INDEX IX_SpiSentRoute_Echo ON dbo.SpiSentRoute (EchoDataHash, CreatedAt)
        INCLUDE (EchoAnsweredAt) WHERE EchoDataHash IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiSentRoute_CreatedAt' AND object_id = OBJECT_ID('dbo.SpiSentRoute'))
    CREATE INDEX IX_SpiSentRoute_CreatedAt ON dbo.SpiSentRoute (CreatedAt);
GO

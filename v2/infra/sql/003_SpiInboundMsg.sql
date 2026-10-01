-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- Every message pulled from Bacen, stored untouched with the routing decision. PiResourceId is the
-- idempotency key: a redelivery from Bacen hits the primary key and keeps the first decision.
IF OBJECT_ID('dbo.SpiInboundMsg', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpiInboundMsg
    (
        PiResourceId  VARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
        MsgId         VARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
        MsgType       VARCHAR(20)    NOT NULL,
        TxKey         VARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
        OriginalMsgId VARCHAR(64) COLLATE Latin1_General_BIN2 NULL,
        RouteSource   VARCHAR(30)    NOT NULL,
        Targets       VARCHAR(30)    NOT NULL,
        Payload       VARBINARY(MAX) NOT NULL,
        ReceivedAt    DATETIME2      NOT NULL CONSTRAINT DF_SpiInboundMsg_ReceivedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SpiInboundMsg PRIMARY KEY CLUSTERED (PiResourceId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiInboundMsg_ReceivedAt' AND object_id = OBJECT_ID('dbo.SpiInboundMsg'))
    CREATE INDEX IX_SpiInboundMsg_ReceivedAt ON dbo.SpiInboundMsg (ReceivedAt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiInboundMsg_TxKey' AND object_id = OBJECT_ID('dbo.SpiInboundMsg'))
    CREATE INDEX IX_SpiInboundMsg_TxKey ON dbo.SpiInboundMsg (TxKey) WHERE TxKey IS NOT NULL;
GO

-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- Per-system delivery queue (queue of record). One row per (message, target system). A pull stream
-- leases rows (LeaseStreamId/LeaseUntil); the ack sets AckedAt; an expired lease is delivered again.
IF OBJECT_ID('dbo.SpiInboundDelivery', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpiInboundDelivery
    (
        Seq           BIGINT IDENTITY(1,1) NOT NULL,
        PiResourceId  VARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
        SystemName    VARCHAR(10) NOT NULL,
        LeaseStreamId VARCHAR(32) NULL,
        LeaseUntil    DATETIME2   NULL,
        DeliveryCount INT         NOT NULL CONSTRAINT DF_SpiInboundDelivery_DeliveryCount DEFAULT 0,
        AckedAt       DATETIME2   NULL,
        CreatedAt     DATETIME2   NOT NULL CONSTRAINT DF_SpiInboundDelivery_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SpiInboundDelivery PRIMARY KEY NONCLUSTERED (PiResourceId, SystemName),
        CONSTRAINT FK_SpiInboundDelivery_Msg FOREIGN KEY (PiResourceId)
            REFERENCES dbo.SpiInboundMsg (PiResourceId) ON DELETE CASCADE
    );

    CREATE UNIQUE CLUSTERED INDEX CX_SpiInboundDelivery_Seq ON dbo.SpiInboundDelivery (Seq);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiInboundDelivery_Pending' AND object_id = OBJECT_ID('dbo.SpiInboundDelivery'))
    CREATE INDEX IX_SpiInboundDelivery_Pending ON dbo.SpiInboundDelivery (SystemName, Seq)
        INCLUDE (LeaseUntil, LeaseStreamId) WHERE AckedAt IS NULL;
GO

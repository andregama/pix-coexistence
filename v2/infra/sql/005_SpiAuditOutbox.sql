-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- Audit events waiting to be published to Kafka. Never read by the message path.
IF OBJECT_ID('dbo.SpiAuditOutbox', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpiAuditOutbox
    (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        Topic       VARCHAR(100)  NOT NULL,
        MessageKey  VARCHAR(64)   NOT NULL,
        Payload     NVARCHAR(MAX) NOT NULL,
        CreatedAt   DATETIME2     NOT NULL CONSTRAINT DF_SpiAuditOutbox_CreatedAt DEFAULT SYSUTCDATETIME(),
        PublishedAt DATETIME2     NULL,
        CONSTRAINT PK_SpiAuditOutbox PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiAuditOutbox_Pending' AND object_id = OBJECT_ID('dbo.SpiAuditOutbox'))
    CREATE INDEX IX_SpiAuditOutbox_Pending ON dbo.SpiAuditOutbox (Id) WHERE PublishedAt IS NULL;
GO

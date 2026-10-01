-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- Binds a transaction key (EndToEndId, RtrId, recurrence id) to the system handling it. The first
-- writer wins, whether the transaction started with a message we sent or one we received. Kept
-- longer than the message tables: a return can arrive long after the original payment.
IF OBJECT_ID('dbo.SpiTxAffinity', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpiTxAffinity
    (
        KeyKind    VARCHAR(8)   NOT NULL,
        TxKey      VARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,
        SystemName VARCHAR(10)  NOT NULL,
        Origin     VARCHAR(10)  NOT NULL,
        CreatedAt  DATETIME2    NOT NULL CONSTRAINT DF_SpiTxAffinity_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SpiTxAffinity PRIMARY KEY CLUSTERED (KeyKind, TxKey)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SpiTxAffinity_CreatedAt' AND object_id = OBJECT_ID('dbo.SpiTxAffinity'))
    CREATE INDEX IX_SpiTxAffinity_CreatedAt ON dbo.SpiTxAffinity (CreatedAt);
GO

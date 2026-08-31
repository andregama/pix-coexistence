SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 10: Consumption tracking (coexistence analytics)
-- Records durable evidence that System B actually pulled + acked an inbound message,
-- distinct from it merely being signed & enqueued (XmlMsgSystemB IS NOT NULL).
--   PiResourceId — the outbound-stream resource id assigned at enqueue time; lets the
--                  ack path map the acked stream ids back to the SpiReceivedMsg row.
--   ConsumedAt   — UTC timestamp set when B's pull/ack committed the message.
-- Idempotent: guarded with IF COL_LENGTH ... IS NULL.

IF OBJECT_ID('dbo.SpiReceivedMsg', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.SpiReceivedMsg', 'PiResourceId') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [PiResourceId] VARCHAR(255) NULL;
GO

IF OBJECT_ID('dbo.SpiReceivedMsg', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.SpiReceivedMsg', 'ConsumedAt') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [ConsumedAt] DATETIME2 NULL;
GO

IF OBJECT_ID('dbo.SpiReceivedMsg', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.SpiReceivedMsg', 'PiResourceId') IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = 'IX_SpiReceivedMsg_PiResourceId' AND object_id = OBJECT_ID('dbo.SpiReceivedMsg')
    )
    CREATE NONCLUSTERED INDEX [IX_SpiReceivedMsg_PiResourceId]
        ON [dbo].[SpiReceivedMsg] ([PiResourceId] ASC)
        WHERE [PiResourceId] IS NOT NULL;
GO

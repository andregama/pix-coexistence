SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 10: SpiReceivedMsg composite key (IdempotentId, MsgType)
-- A single correlation id can carry a primary message (pacs.008 credit) and a later response
-- (pacs.002) that shares its EndToEndId. Keyed by IdempotentId alone, the response overwrote the
-- credit's row. Re-key on (IdempotentId, MsgType) so each message is its own row.
-- Safe on existing data: every IdempotentId is currently unique, so (IdempotentId, MsgType) is too.
-- Idempotent: only acts when the current PK is the single-column key.

IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    WHERE kc.name = 'PK_SpiReceivedMsg'
      AND kc.parent_object_id = OBJECT_ID('dbo.SpiReceivedMsg')
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id) = 1
)
BEGIN
    ALTER TABLE [dbo].[SpiReceivedMsg] DROP CONSTRAINT [PK_SpiReceivedMsg];
    ALTER TABLE [dbo].[SpiReceivedMsg]
        ADD CONSTRAINT [PK_SpiReceivedMsg] PRIMARY KEY CLUSTERED ([IdempotentId] ASC, [MsgType] ASC);
END;
GO

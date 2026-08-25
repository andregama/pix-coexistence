SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- Phase 9: Correlation-source marker (RF-05)
-- Records which key-strategy each correlation row used:
--   'MessageKey' — the shared Bacen message-level idempotency key (EndToEndId / RtrId / MsgId).
--   'DerivedKey' — a key derived from a shared business field (recurrenceId, or the original
--                  payment's OrgnlEndToEndId) for the Pix Automático / cancellation families whose
--                  message-level keys the orchestrator cannot align across System A and System B.
-- Idempotent: guarded with IF COL_LENGTH ... IS NULL.

IF OBJECT_ID('dbo.SpiSentMsg', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.SpiSentMsg', 'CorrelationSource') IS NULL
    ALTER TABLE [dbo].[SpiSentMsg] ADD [CorrelationSource] VARCHAR(20) NULL;
GO

IF OBJECT_ID('dbo.SpiReceivedMsg', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.SpiReceivedMsg', 'CorrelationSource') IS NULL
    ALTER TABLE [dbo].[SpiReceivedMsg] ADD [CorrelationSource] VARCHAR(20) NULL;
GO

-- sqlcmd defaults to QUOTED_IDENTIFIER OFF, which filtered indexes do not accept.
SET QUOTED_IDENTIFIER ON;
GO

-- What each system receives is signed by the bank's HSM, and for a message carrying transactions
-- of both systems each one receives a different (split) message. So the delivered content belongs
-- to the delivery; SpiInboundMsg.Payload keeps what Bacen sent, with Bacen's signature.
-- NULL only on rows written before this column existed: they are delivered with the message payload.
IF COL_LENGTH('dbo.SpiInboundDelivery', 'Payload') IS NULL
    ALTER TABLE dbo.SpiInboundDelivery ADD Payload VARBINARY(MAX) NULL;
GO

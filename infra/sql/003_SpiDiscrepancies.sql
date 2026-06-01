-- Table: SpiDiscrepancies
-- Field-level differences detected by spi-comparison-engine between System A and System B.

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'SpiDiscrepancies' AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE [dbo].[SpiDiscrepancies] (
        [Id]                UNIQUEIDENTIFIER NOT NULL,
        [IdSystemA]         NVARCHAR(64)     NOT NULL,
        [IdSystemB]         NVARCHAR(64)     NOT NULL,
        [CorrelationSource] NVARCHAR(32)     NOT NULL,
        [Field]             NVARCHAR(64)     NOT NULL,
        [SystemAValue]      NVARCHAR(512)    NULL,
        [SystemBValue]      NVARCHAR(512)    NULL,
        [DetectedAt]        DATETIME2        NOT NULL,
        CONSTRAINT [PK_SpiDiscrepancies] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SpiDiscrepancies_IdSystemA_DetectedAt' AND object_id = OBJECT_ID('dbo.SpiDiscrepancies')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SpiDiscrepancies_IdSystemA_DetectedAt]
        ON [dbo].[SpiDiscrepancies] ([IdSystemA] ASC, [DetectedAt] ASC);
END;
GO

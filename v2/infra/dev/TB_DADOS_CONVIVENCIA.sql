-- LOCAL DEVELOPMENT ONLY. The registry of customers assigned to System B already exists in the
-- coexistence database of every shared environment; this script recreates its shape so the proxy
-- can run against a local SQL Server. Never apply it to a shared environment.
IF OBJECT_ID('dbo.TB_DADOS_CONVIVENCIA', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TB_DADOS_CONVIVENCIA
    (
        Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Conta       VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
        Agencia     VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
        Documento   VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
        DataCriacao DATETIME2 NOT NULL
    );
END
GO

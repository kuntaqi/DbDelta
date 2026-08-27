IF DB_ID('DbDelta_Demo_Src') IS NOT NULL BEGIN ALTER DATABASE DbDelta_Demo_Src SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE DbDelta_Demo_Src; END
IF DB_ID('DbDelta_Demo_Tgt') IS NOT NULL BEGIN ALTER DATABASE DbDelta_Demo_Tgt SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE DbDelta_Demo_Tgt; END
CREATE DATABASE DbDelta_Demo_Src;
CREATE DATABASE DbDelta_Demo_Tgt;
GO
USE DbDelta_Demo_Src;
GO
CREATE SCHEMA sales;
GO
CREATE TABLE dbo.Category (CategoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
CREATE TABLE dbo.Company (
    CompanyId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Company PRIMARY KEY,
    CompanyName NVARCHAR(200) NOT NULL,
    Segment NVARCHAR(40) NULL,
    CategoryId INT NULL CONSTRAINT FK_Company_Category REFERENCES dbo.Category(CategoryId),
    RatingBand TINYINT NULL,
    Total DECIMAL(18,2) NOT NULL CONSTRAINT DF_Company_Total DEFAULT (0),
    CONSTRAINT CK_Company_Rating CHECK (RatingBand IS NULL OR RatingBand BETWEEN 1 AND 5)
);
CREATE TABLE dbo.Contact (ContactId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contact PRIMARY KEY, CompanyId INT NOT NULL CONSTRAINT FK_Contact_Company REFERENCES dbo.Company(CompanyId), Email NVARCHAR(320) NOT NULL);
GO
CREATE INDEX IX_Company_Rating ON dbo.Company (RatingBand) INCLUDE (CompanyName);
GO
CREATE VIEW sales.vCompanySegment AS SELECT CompanyId, CompanyName, Segment FROM dbo.Company;
GO
CREATE PROCEDURE dbo.usp_GetCompany @Id INT AS SELECT * FROM dbo.Company WHERE CompanyId = @Id;
GO
SET IDENTITY_INSERT dbo.Category ON;
INSERT INTO dbo.Category (CategoryId, Name) VALUES (1,N'Retail'),(2,N'Wholesale'),(3,N'Energy');
SET IDENTITY_INSERT dbo.Category OFF;
GO
USE DbDelta_Demo_Tgt;
GO
CREATE SCHEMA sales;
GO
CREATE TABLE dbo.Category (CategoryId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
CREATE TABLE dbo.Company (
    CompanyId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Company PRIMARY KEY,
    CompanyName NVARCHAR(200) NOT NULL,
    Segment NVARCHAR(20) NULL,
    CategoryId INT NULL CONSTRAINT FK_Company_Category REFERENCES dbo.Category(CategoryId),
    Total DECIMAL(18,2) NOT NULL CONSTRAINT DF_Company_Total DEFAULT (0)
);
CREATE TABLE dbo.SegmentLegacy (Id INT NOT NULL CONSTRAINT PK_SegmentLegacy PRIMARY KEY);
GO
CREATE VIEW sales.vCompanySegment AS SELECT CompanyId, CompanyName FROM dbo.Company;
GO
CREATE PROCEDURE dbo.usp_GetCompany @Id INT AS SELECT * FROM dbo.Company WHERE CompanyId = @Id;
GO
SET IDENTITY_INSERT dbo.Category ON;
INSERT INTO dbo.Category (CategoryId, Name) VALUES (1,N'Retail'),(2,N'Wholesale Ltd'),(4,N'Obsolete');
SET IDENTITY_INSERT dbo.Category OFF;
GO

-- Two keyless tables, because "no primary key" is two different situations and both need to be
-- reachable from a fresh clone. dbo.Ledger has a key the schema already declares; dbo.AuditTrail has
-- nothing declared, so its candidates have to be measured.
USE DbDelta_Demo_Src;
GO
CREATE TABLE dbo.Ledger (Ref NVARCHAR(20) NOT NULL CONSTRAINT UQ_Ledger_Ref UNIQUE, Amount DECIMAL(18,2) NOT NULL);
CREATE TABLE dbo.AuditTrail (EventId INT NOT NULL, Area NVARCHAR(40) NOT NULL, Note NVARCHAR(200) NULL);
GO
INSERT INTO dbo.Ledger (Ref, Amount) VALUES (N'A1', 10), (N'A2', 20);
INSERT INTO dbo.AuditTrail (EventId, Area, Note) VALUES (1, N'load', N'first'), (2, N'load', NULL), (3, N'sync', N'third');
GO
USE DbDelta_Demo_Tgt;
GO
CREATE TABLE dbo.Ledger (Ref NVARCHAR(20) NOT NULL CONSTRAINT UQ_Ledger_Ref UNIQUE, Amount DECIMAL(18,2) NOT NULL);
CREATE TABLE dbo.AuditTrail (EventId INT NOT NULL, Area NVARCHAR(40) NOT NULL, Note NVARCHAR(200) NULL);
GO
INSERT INTO dbo.Ledger (Ref, Amount) VALUES (N'A1', 10), (N'A2', 99);
INSERT INTO dbo.AuditTrail (EventId, Area, Note) VALUES (1, N'load', N'first'), (2, N'load', NULL), (3, N'sync', N'third');
GO

-- Both source-only, and Site's key points at Region: ticking Site alone is the case dependency
-- closure exists for. Without it the script creates Site and then adds an FK to a table that is not
-- there, which fails on apply and rolls the whole transaction back.
USE DbDelta_Demo_Src;
GO
CREATE TABLE dbo.Region (RegionId INT NOT NULL CONSTRAINT PK_Region PRIMARY KEY, Name NVARCHAR(60) NOT NULL);
CREATE TABLE dbo.Site (
    SiteId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Site PRIMARY KEY,
    RegionId INT NOT NULL CONSTRAINT FK_Site_Region REFERENCES dbo.Region(RegionId),
    SiteName NVARCHAR(120) NOT NULL
);
GO
INSERT INTO dbo.Region (RegionId, Name) VALUES (1, N'North'), (2, N'South');
INSERT INTO dbo.Site (RegionId, SiteName) VALUES (1, N'North depot'), (2, N'South depot'), (1, N'North annex');
GO

-- Company rows for parent closure to walk. Contoso sits in category 3, which the target does not have,
-- so picking dbo.Company's data pulls that one category row in ahead of the companies that need it.
USE DbDelta_Demo_Src;
GO
SET IDENTITY_INSERT dbo.Company ON;
INSERT INTO dbo.Company (CompanyId, CompanyName, Segment, CategoryId, RatingBand, Total)
VALUES (1, N'Northwind', N'Retail', 1, 3, 100.00), (2, N'Contoso', N'Energy', 3, 4, 250.00);
SET IDENTITY_INSERT dbo.Company OFF;
GO

-- A table big enough to take the staged path. 1200 rows of literal INSERT statements is a script nobody
-- reads, so past Safety:MaxInlineTableBytes the rows go over the wire into a staging table instead and
-- the script stays four statements long. Lower that setting to see it on a table this size.
USE DbDelta_Demo_Src;
GO
CREATE TABLE dbo.Metric (
    MetricId INT NOT NULL CONSTRAINT PK_Metric PRIMARY KEY,
    Label    NVARCHAR(60) NOT NULL,
    Amount   DECIMAL(18,2) NOT NULL,
    At       DATETIME2 NULL
);
GO
INSERT INTO dbo.Metric (MetricId, Label, Amount, At)
SELECT TOP (1200)
    ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
    N'row ' + CONVERT(nvarchar(10), ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
    ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) * 1.5,
    DATEADD(minute, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2026-01-01T00:00:00')
FROM sys.all_objects a CROSS JOIN sys.all_objects b;
GO
USE DbDelta_Demo_Tgt;
GO
CREATE TABLE dbo.Metric (
    MetricId INT NOT NULL CONSTRAINT PK_Metric PRIMARY KEY,
    Label    NVARCHAR(60) NOT NULL,
    Amount   DECIMAL(18,2) NOT NULL,
    At       DATETIME2 NULL
);
GO

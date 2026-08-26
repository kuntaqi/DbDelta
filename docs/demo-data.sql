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

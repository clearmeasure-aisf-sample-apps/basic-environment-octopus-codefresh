-- Sandbox fixture: the canary row that conformance tests write before a disruption (sleep,
-- rebuild, restore, password rotation) and read afterwards (CAP-GIT-011, CAP-AZ-008, CAP-AZ-010).
IF OBJECT_ID(N'dbo.Canary', N'U') IS NULL
    CREATE TABLE dbo.Canary (
        Id int NOT NULL CONSTRAINT PK_Canary PRIMARY KEY,
        [Value] nvarchar(200) NOT NULL,
        UpdatedAtUtc datetime2 NOT NULL);
GO

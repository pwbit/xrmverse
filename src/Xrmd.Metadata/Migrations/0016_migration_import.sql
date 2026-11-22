-- Migration from Dataverse (migration spec §7.3-§7.8, plan M4a): the import ledger of a run.
-- meta.migration_idmap: source id -> target id of the rows that already exist in the target (root business unit, default teams,
--   SYSTEM/INTEGRATION users, base currency, roles, users mapped onto existing ones); applied to every lookup value.
-- meta.migration_deferred: lookups left out of a record because their target was not loaded yet (cycles, self references),
--   written in the transaction of the record, set by the deferred-lookups phase.
-- meta.migration_issue: what the report lists (failed rows, nulled lookups, dropped columns, users created by action).
-- meta.migration_phase: when each phase ran.
-- meta.migration_table: the package's tables and exported counts.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.migration_idmap', N'U') IS NULL
    CREATE TABLE meta.migration_idmap (
        runid    uniqueidentifier NOT NULL,
        sourceid uniqueidentifier NOT NULL,
        targetid uniqueidentifier NOT NULL,
        CONSTRAINT PK_meta_migration_idmap PRIMARY KEY (runid, sourceid)
    );
GO

IF OBJECT_ID(N'meta.migration_deferred', N'U') IS NULL
    CREATE TABLE meta.migration_deferred (
        seq        bigint IDENTITY(1,1) NOT NULL,
        runid      uniqueidentifier NOT NULL,
        tablename  nvarchar(128)    NOT NULL,
        recordid   uniqueidentifier NOT NULL,
        columnname nvarchar(128)    NOT NULL,
        reftable   nvarchar(128)    NOT NULL,
        refid      uniqueidentifier NOT NULL,
        modifiedon datetime2        NULL,
        CONSTRAINT PK_meta_migration_deferred PRIMARY KEY (runid, tablename, recordid, columnname)
    );
GO

IF OBJECT_ID(N'meta.migration_issue', N'U') IS NULL
    CREATE TABLE meta.migration_issue (
        issueid    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_meta_migration_issue PRIMARY KEY,
        runid      uniqueidentifier NOT NULL,
        phase      int              NOT NULL,
        severity   nvarchar(16)     NOT NULL,
        code       nvarchar(64)     NOT NULL,
        tablename  nvarchar(128)    NULL,
        recordid   uniqueidentifier NULL,
        columnname nvarchar(128)    NULL,
        message    nvarchar(2000)   NOT NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.migration_issue') AND name = N'IX_meta_migration_issue_run')
    CREATE INDEX IX_meta_migration_issue_run ON meta.migration_issue (runid, severity);
GO

-- The ledger records an issue once per run: it looks for the same one by code and record first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.migration_issue') AND name = N'IX_meta_migration_issue_record')
    CREATE INDEX IX_meta_migration_issue_record ON meta.migration_issue (runid, code, recordid);
GO

IF OBJECT_ID(N'meta.migration_phase', N'U') IS NULL
    CREATE TABLE meta.migration_phase (
        runid     uniqueidentifier NOT NULL,
        phase     int              NOT NULL,
        name      nvarchar(64)     NOT NULL,
        startedon datetime2        NOT NULL,
        endedon   datetime2        NULL,
        CONSTRAINT PK_meta_migration_phase PRIMARY KEY (runid, phase)
    );
GO

-- meta.migration_table: the tables of the run's package and their exported record counts (the report's "exported").
IF OBJECT_ID(N'meta.migration_table', N'U') IS NULL
    CREATE TABLE meta.migration_table (
        runid     uniqueidentifier NOT NULL,
        tablename nvarchar(128)    NOT NULL,
        exported  bigint           NOT NULL,
        CONSTRAINT PK_meta_migration_table PRIMARY KEY (runid, tablename)
    );
GO

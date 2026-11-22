-- Migration from Dataverse (migration spec §7, plan M3): the ledger of the import side.
-- meta.migration_package: a package uploaded in chunks to the file store (migrate/packages/{id}/...), uploading or complete.
-- meta.migration_run: one import run; its state drives the organization's migration lock (on in every state but 'finalized'), and
--   the filtered unique index keeps at most one run that is not finalized. phase is the number of phases completed, so a resumed
--   run starts at the next one. options never holds the package passphrase (it lives only in the server's memory).
-- meta.migration_chunk: the package entries a run has committed, written in the transaction of their rows, so a retry skips them.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.migration_package', N'U') IS NULL
    CREATE TABLE meta.migration_package (
        packageid uniqueidentifier NOT NULL CONSTRAINT PK_meta_migration_package PRIMARY KEY,
        state     nvarchar(16)     NOT NULL,
        bytes     bigint           NULL,
        sha256    char(64)         NULL,
        createdby uniqueidentifier NOT NULL,
        createdon datetime2        NOT NULL
    );
GO

IF OBJECT_ID(N'meta.migration_run', N'U') IS NULL
    CREATE TABLE meta.migration_run (
        runid            uniqueidentifier NOT NULL CONSTRAINT PK_meta_migration_run PRIMARY KEY,
        packageid        uniqueidentifier NULL,
        serverpath       nvarchar(1024)   NULL,
        state            nvarchar(32)     NOT NULL,
        phase            int              NOT NULL CONSTRAINT DF_meta_migration_run_phase DEFAULT 0,
        options          nvarchar(max)    NOT NULL,
        asyncoperationid uniqueidentifier NULL,
        message          nvarchar(2000)   NULL,
        createdby        uniqueidentifier NOT NULL,
        createdon        datetime2        NOT NULL,
        startedon        datetime2        NULL,
        endedon          datetime2        NULL,
        finalizedon      datetime2        NULL,
        isopen           AS CAST(1 AS bit) PERSISTED
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.migration_run') AND name = N'UX_meta_migration_run_open')
    CREATE UNIQUE INDEX UX_meta_migration_run_open ON meta.migration_run (isopen) WHERE state <> 'finalized';
GO

IF OBJECT_ID(N'meta.migration_chunk', N'U') IS NULL
    CREATE TABLE meta.migration_chunk (
        runid       uniqueidentifier NOT NULL,
        entry       nvarchar(400)    NOT NULL,
        phase       int              NOT NULL,
        rows        int              NOT NULL,
        failedrows  int              NOT NULL,
        committedon datetime2        NOT NULL,
        CONSTRAINT PK_meta_migration_chunk PRIMARY KEY (runid, entry)
    );
GO

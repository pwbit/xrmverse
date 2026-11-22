-- Plan M4b (migration spec §7.9): the plug-in steps, webhook steps and processes an import's solutions brought in active, which the
-- schema phase left off. `xrmd migrate activate` (M5) reads them; nothing else turns them back on.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.migration_deactivated', N'U') IS NULL
    CREATE TABLE meta.migration_deactivated (
        runid        uniqueidentifier NOT NULL,
        tablename    nvarchar(128)    NOT NULL,
        id           uniqueidentifier NOT NULL,
        name         nvarchar(400)    NOT NULL,
        sourcestate  int              NOT NULL,
        sourcestatus int              NOT NULL,
        CONSTRAINT PK_meta_migration_deactivated PRIMARY KEY (runid, tablename, id)
    );
GO

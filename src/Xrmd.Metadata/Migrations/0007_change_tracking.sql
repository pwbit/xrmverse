-- Change tracking (plan 2.13). The tables with ChangeTrackingEnabled keep a row here with the version at which tracking
-- started (a token older than it is expired: deletions before it were never logged); the row goes with the table.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.change_tracking_table', N'U') IS NULL
    CREATE TABLE meta.change_tracking_table (
        entity_logical_name nvarchar(128) NOT NULL CONSTRAINT PK_meta_change_tracking_table PRIMARY KEY,
        enabled_version     bigint        NOT NULL
    );
GO

-- The deletion log: one row per record deleted from a tracked table, ordered by its own rowversion. Purged by age
-- (organization.expirechangetrackingindays); the rows of a table go with the table.

IF OBJECT_ID(N'meta.deleted_record', N'U') IS NULL
    CREATE TABLE meta.deleted_record (
        version             rowversion       NOT NULL,
        entity_logical_name nvarchar(128)    NOT NULL,
        object_id           uniqueidentifier NOT NULL,
        deleted_on          datetime2        NOT NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.deleted_record') AND name = N'CX_meta_deleted_record_version')
    CREATE UNIQUE CLUSTERED INDEX CX_meta_deleted_record_version ON meta.deleted_record (version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.deleted_record') AND name = N'IX_meta_deleted_record_deleted_on')
    CREATE INDEX IX_meta_deleted_record_deleted_on ON meta.deleted_record (deleted_on);
GO

-- One row: the highest log version the purge removed (a token below it is expired), and the UTC time the database was last
-- restored from a backup (a token issued before it is expired; null when it never was).

IF OBJECT_ID(N'meta.change_tracking_state', N'U') IS NULL
    CREATE TABLE meta.change_tracking_state (
        purged_version bigint    NOT NULL,
        restored_at    datetime2 NULL
    );
GO

IF COL_LENGTH(N'meta.change_tracking_state', N'restored_at') IS NULL
    ALTER TABLE meta.change_tracking_state ADD restored_at datetime2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM meta.change_tracking_state)
    INSERT INTO meta.change_tracking_state (purged_version) VALUES (0);
GO

-- Background workflow waits (plan 2.14, ruling W10). A workflow job suspended (1/10) on a wait condition keeps one row here: the
-- record its condition reads (entity, objectid) and the time it may run again (wake_at: the wait's timeout, or far in the future
-- without one). A core write of that record sets wake_at to the time of the write, in the write's transaction; the worker's claim
-- skips a job whose row has a wake_at in the future. The row goes when the job runs again, ends or is canceled. seen_version is the
-- versionnumber of the record that the job's run is known to have seen (null: unknown, its first wait): a check after the wait is
-- registered wakes the job only for a later version.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.workflow_wait', N'U') IS NULL
    CREATE TABLE meta.workflow_wait (
        asyncoperationid uniqueidentifier NOT NULL CONSTRAINT PK_meta_workflow_wait PRIMARY KEY,
        entity           nvarchar(128)    NOT NULL,
        objectid         uniqueidentifier NOT NULL,
        wake_at          datetime2        NOT NULL,
        seen_version     bigint           NULL
    );
GO

IF COL_LENGTH(N'meta.workflow_wait', N'seen_version') IS NULL
    ALTER TABLE meta.workflow_wait ADD seen_version bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.workflow_wait') AND name = N'IX_workflow_wait_record')
    CREATE INDEX IX_workflow_wait_record ON meta.workflow_wait (entity, objectid);
GO

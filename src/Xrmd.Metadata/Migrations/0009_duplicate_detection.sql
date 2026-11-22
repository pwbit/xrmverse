-- Duplicate detection (plan 2.15, ruling D20). Each duplicate rule in Publishing or Published has a match code table
-- meta.matchcode_{ruleid:N} (created at publish time, not here); this registry names it with the rule's matching table.
-- publish_job is the asyncoperationid of the publish job that fills the table (the publish's identity): every write of that
-- job is conditional on the registry still naming it, so a job of an earlier publish of the same rule changes nothing.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.matchcode_table', N'U') IS NULL
    CREATE TABLE meta.matchcode_table (
        duplicateruleid uniqueidentifier NOT NULL CONSTRAINT PK_meta_matchcode_table PRIMARY KEY,
        table_name      sysname          NOT NULL,
        matching_entity nvarchar(128)    NOT NULL,
        created_on      datetime2        NOT NULL,
        publish_job     uniqueidentifier NULL
    );
GO

IF COL_LENGTH(N'meta.matchcode_table', N'publish_job') IS NULL
    ALTER TABLE meta.matchcode_table ADD publish_job uniqueidentifier NULL;
GO

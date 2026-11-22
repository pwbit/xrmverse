-- Shares by record and table (plan 2.11, final review C2). A share (data.principalobjectaccess) names its record by objectid
-- and objecttypecode: records of two tables may have the same id (an activity created with an account's id), and each has
-- its own shares. The unique index on (objectid, principalid) becomes one on (objectid, objecttypecode, principalid). Every
-- row that the old index allowed is allowed by the new one, so the change succeeds on existing data.
-- The table is created by the seed after the migrations on a new database, where SecurityBootstrap creates the index; the
-- statements run through EXEC so that the batch compiles without the table. Every step is guarded by an existence check, so
-- the script can be applied again without failing.

IF OBJECT_ID(N'data.principalobjectaccess', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'data.principalobjectaccess') AND name = N'UX_principalobjectaccess_object_type_principal')
    EXEC(N'CREATE UNIQUE INDEX UX_principalobjectaccess_object_type_principal ON data.principalobjectaccess (objectid, objecttypecode, principalid)');
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'data.principalobjectaccess') AND name = N'UX_principalobjectaccess_object_principal')
    EXEC(N'DROP INDEX UX_principalobjectaccess_object_principal ON data.principalobjectaccess');
GO

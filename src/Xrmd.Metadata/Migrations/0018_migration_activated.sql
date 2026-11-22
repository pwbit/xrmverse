-- Plan M5 (migration spec §7.9): when `xrmd migrate activate` switched an item of meta.migration_deactivated back on.
-- Guarded, so the script can be applied again without failing.

IF COL_LENGTH(N'meta.migration_deactivated', N'activatedon') IS NULL
    ALTER TABLE meta.migration_deactivated ADD activatedon datetime2 NULL;
GO

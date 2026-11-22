-- meta.upgrade_step: the one-time data upgrades of an existing organization that already ran (for instance the
-- privileges a later version adds to the standard roles). A step runs only while its name is missing here, so a change
-- an administrator makes afterwards is never undone by a restart.

CREATE TABLE meta.upgrade_step (
    name       nvarchar(128) NOT NULL CONSTRAINT PK_meta_upgrade_step PRIMARY KEY,
    applied_on datetime2     NOT NULL
);
GO

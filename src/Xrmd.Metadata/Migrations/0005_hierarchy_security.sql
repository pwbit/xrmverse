-- Hierarchy security (plan 2.5, ruling H3): the tables the manager hierarchy never applies to. An xrmverse extension
-- (Dataverse does not document the list as a column), set by a System Administrator through the Admin API; the row of a
-- table goes with the table when it is deleted.

CREATE TABLE meta.hierarchy_security_exclusion (
    entity_logical_name nvarchar(128) NOT NULL CONSTRAINT PK_meta_hierarchy_security_exclusion PRIMARY KEY
);
GO

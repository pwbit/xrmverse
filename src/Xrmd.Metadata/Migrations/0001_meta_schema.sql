-- Schema [meta] and meta.schema_version are bootstrapped by SchemaMigrator.

CREATE TABLE meta.entity (
    metadata_id   uniqueidentifier NOT NULL CONSTRAINT PK_meta_entity PRIMARY KEY,
    logical_name  nvarchar(64)     NOT NULL CONSTRAINT UQ_meta_entity_logical_name UNIQUE,
    definition    nvarchar(max)    NOT NULL,
    version_stamp bigint           NOT NULL
);
GO

CREATE TABLE meta.attribute (
    metadata_id         uniqueidentifier NOT NULL CONSTRAINT PK_meta_attribute PRIMARY KEY,
    entity_logical_name nvarchar(64)     NOT NULL,
    logical_name        nvarchar(64)     NOT NULL,
    definition          nvarchar(max)    NOT NULL,
    version_stamp       bigint           NOT NULL,
    CONSTRAINT UQ_meta_attribute_entity_logical UNIQUE (entity_logical_name, logical_name)
);
GO

CREATE TABLE meta.relationship (
    metadata_id   uniqueidentifier NOT NULL CONSTRAINT PK_meta_relationship PRIMARY KEY,
    schema_name   nvarchar(128)    NOT NULL CONSTRAINT UQ_meta_relationship_schema_name UNIQUE,
    definition    nvarchar(max)    NOT NULL,
    version_stamp bigint           NOT NULL
);
GO

CREATE TABLE meta.optionset (
    metadata_id   uniqueidentifier NOT NULL CONSTRAINT PK_meta_optionset PRIMARY KEY,
    name          nvarchar(128)    NOT NULL CONSTRAINT UQ_meta_optionset_name UNIQUE,
    definition    nvarchar(max)    NOT NULL,
    version_stamp bigint           NOT NULL
);
GO

CREATE TABLE meta.version_stamp (
    [current] bigint NOT NULL
);
GO

INSERT INTO meta.version_stamp ([current]) VALUES (0);
GO

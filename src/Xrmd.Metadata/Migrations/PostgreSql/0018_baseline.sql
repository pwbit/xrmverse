-- PostgreSQL baseline (plan 2.7a, ruling P6): the meta and auth schemas as SQL Server has them after migrations 0001-0018,
-- translated by hand, and the record of versions 1-18. From 0019 on every migration has a file here and one in Migrations/
-- with the same name (MigrationParityTests).
--
-- The schemas meta, data and auth, the citext extension, data.versionnumber_seq, data.next_versionnumber() and
-- data.set_versionnumber() come from the
-- organization bootstrap (PostgresDialect.PrepareDatabaseAsync), meta.schema_version from SchemaMigrator.
-- Types (Global Constraints): uniqueidentifier -> uuid, nvarchar/varchar/char/sysname -> citext (case-insensitive like the
-- _CI_AS columns, ruling P5; lengths are enforced by validation, not by the type), the meta JSON definition columns -> text,
-- datetime2 -> timestamptz, varbinary -> bytea, bit -> boolean, IDENTITY -> GENERATED ALWAYS AS IDENTITY. Constraint and index
-- names are quoted so that they keep SQL Server's names.

-- 0001 meta schema -----------------------------------------------------------------------------------------------------------

CREATE TABLE meta.entity (
    metadata_id   uuid   NOT NULL CONSTRAINT "PK_meta_entity" PRIMARY KEY,
    logical_name  citext NOT NULL CONSTRAINT "UQ_meta_entity_logical_name" UNIQUE,
    definition    text   NOT NULL,
    version_stamp bigint NOT NULL,
    -- 0003
    unmodelled_xml citext NULL
);

CREATE TABLE meta.attribute (
    metadata_id         uuid   NOT NULL CONSTRAINT "PK_meta_attribute" PRIMARY KEY,
    entity_logical_name citext NOT NULL,
    logical_name        citext NOT NULL,
    definition          text   NOT NULL,
    version_stamp       bigint NOT NULL,
    -- 0003
    unmodelled_xml      citext NULL,
    CONSTRAINT "UQ_meta_attribute_entity_logical" UNIQUE (entity_logical_name, logical_name)
);

CREATE TABLE meta.relationship (
    metadata_id         uuid   NOT NULL CONSTRAINT "PK_meta_relationship" PRIMARY KEY,
    schema_name         citext NOT NULL CONSTRAINT "UQ_meta_relationship_schema_name" UNIQUE,
    definition          text   NOT NULL,
    version_stamp       bigint NOT NULL,
    -- 0002
    associated_menu_xml citext NULL,
    -- 0003
    unmodelled_xml      citext NULL
);

CREATE TABLE meta.optionset (
    metadata_id    uuid   NOT NULL CONSTRAINT "PK_meta_optionset" PRIMARY KEY,
    name           citext NOT NULL CONSTRAINT "UQ_meta_optionset_name" UNIQUE,
    definition     text   NOT NULL,
    version_stamp  bigint NOT NULL,
    -- 0003
    unmodelled_xml citext NULL
);

CREATE TABLE meta.version_stamp (
    "current" bigint NOT NULL
);

INSERT INTO meta.version_stamp ("current") VALUES (0);

-- 0004 file sessions ---------------------------------------------------------------------------------------------------------

CREATE TABLE meta.filesession (
    id                     uuid        NOT NULL CONSTRAINT "PK_meta_filesession" PRIMARY KEY,
    kind                   citext      NOT NULL CONSTRAINT "CK_meta_filesession_kind" CHECK (kind IN ('upload', 'download')),
    caller_id              uuid        NOT NULL,
    entity_logical_name    citext      NOT NULL,
    record_id              uuid        NOT NULL,
    attribute_logical_name citext      NOT NULL,
    file_name              citext      NULL,
    created_on             timestamptz NOT NULL,
    last_used_on           timestamptz NOT NULL,
    fileattachment_id      uuid        NULL,
    block_ids              citext      NOT NULL
);

CREATE INDEX "IX_meta_filesession_last_used_on" ON meta.filesession (last_used_on);

-- The secret the continuation tokens are derived from: 32 random bytes, written once, never sent. gen_random_uuid() draws from
-- the server's strong random source (SQL Server: CRYPT_GEN_RANDOM(32)); two of them give the 32 bytes without pgcrypto.
CREATE TABLE meta.filesecret (
    secret bytea NOT NULL
);

INSERT INTO meta.filesecret (secret) VALUES (uuid_send(gen_random_uuid()) || uuid_send(gen_random_uuid()));

CREATE TABLE meta.fileorphan (
    blob_key   citext      NOT NULL CONSTRAINT "PK_meta_fileorphan" PRIMARY KEY,
    first_seen timestamptz NOT NULL
);

-- 0005 hierarchy security ----------------------------------------------------------------------------------------------------

CREATE TABLE meta.hierarchy_security_exclusion (
    entity_logical_name citext NOT NULL CONSTRAINT "PK_meta_hierarchy_security_exclusion" PRIMARY KEY
);

-- 0006 upgrade steps ---------------------------------------------------------------------------------------------------------

CREATE TABLE meta.upgrade_step (
    name       citext      NOT NULL CONSTRAINT "PK_meta_upgrade_step" PRIMARY KEY,
    applied_on timestamptz NOT NULL
);

-- 0007 change tracking -------------------------------------------------------------------------------------------------------

CREATE TABLE meta.change_tracking_table (
    entity_logical_name citext NOT NULL CONSTRAINT "PK_meta_change_tracking_table" PRIMARY KEY,
    enabled_version     bigint NOT NULL
);

-- The deletion log, ordered by its own version: SQL Server's rowversion is the database's one counter, shared with the data
-- tables' versionnumber; here the version comes from the same organization sequence (ruling P4), drawn through
-- data.next_versionnumber(), which publishes the drawing transaction to change tracking's bound first (plan 2.7a, Task 7).
CREATE TABLE meta.deleted_record (
    version             bigint      NOT NULL DEFAULT "data"."next_versionnumber"(),
    entity_logical_name citext      NOT NULL,
    object_id           uuid        NOT NULL,
    deleted_on          timestamptz NOT NULL
);

CREATE UNIQUE INDEX "CX_meta_deleted_record_version" ON meta.deleted_record (version);
CREATE INDEX "IX_meta_deleted_record_deleted_on" ON meta.deleted_record (deleted_on);

CREATE TABLE meta.change_tracking_state (
    purged_version bigint      NOT NULL,
    restored_at    timestamptz NULL
);

INSERT INTO meta.change_tracking_state (purged_version) VALUES (0);

-- 0008 component layers ------------------------------------------------------------------------------------------------------

CREATE TABLE meta.component_layer (
    component_type integer     NOT NULL,
    object_id      uuid        NOT NULL,
    solution_id    uuid        NOT NULL,
    layer_order    bigint      NOT NULL,
    payload        citext      NULL,
    created_on     timestamptz NOT NULL DEFAULT statement_timestamp(),
    CONSTRAINT "PK_meta_component_layer" PRIMARY KEY (component_type, object_id, solution_id)
);

CREATE INDEX "IX_meta_component_layer_solution" ON meta.component_layer (solution_id);
CREATE INDEX "IX_meta_component_layer_object" ON meta.component_layer (object_id);

CREATE TABLE meta.solution_rank (
    solution_id uuid    NOT NULL CONSTRAINT "PK_meta_solution_rank" PRIMARY KEY,
    segment     integer NOT NULL,
    sub         integer NOT NULL,
    CONSTRAINT "CK_meta_solution_rank_sub" CHECK (sub BETWEEN 0 AND 1023),
    CONSTRAINT "CK_meta_solution_rank_segment" CHECK (segment > 0)
);

-- 0009 duplicate detection ---------------------------------------------------------------------------------------------------

CREATE TABLE meta.matchcode_table (
    duplicateruleid uuid        NOT NULL CONSTRAINT "PK_meta_matchcode_table" PRIMARY KEY,
    table_name      citext      NOT NULL,
    matching_entity citext      NOT NULL,
    created_on      timestamptz NOT NULL,
    publish_job     uuid        NULL
);

-- 0010 local identity provider (the auth schema; column names as the EF model writes them) -------------------------------------

CREATE TABLE auth.application (
    "Id" citext NOT NULL,
    "ApplicationType" citext NULL,
    "ClientId" citext NULL,
    "ClientSecret" citext NULL,
    "ClientType" citext NULL,
    "ConcurrencyToken" citext NULL,
    "ConsentType" citext NULL,
    "DisplayName" citext NULL,
    "DisplayNames" citext NULL,
    "JsonWebKeySet" citext NULL,
    "Permissions" citext NULL,
    "PostLogoutRedirectUris" citext NULL,
    "Properties" citext NULL,
    "RedirectUris" citext NULL,
    "Requirements" citext NULL,
    "Settings" citext NULL,
    CONSTRAINT "PK_application" PRIMARY KEY ("Id")
);

CREATE TABLE auth.credential (
    "SystemUserId" uuid NOT NULL,
    "UserName" citext NOT NULL,
    "PasswordHash" citext NOT NULL,
    "TotpKey" bytea NULL,
    "TotpLastStep" bigint NULL,
    "FailedCount" integer NOT NULL,
    "LockedUntilUtc" timestamptz NULL,
    "ChangedOnUtc" timestamptz NOT NULL,
    CONSTRAINT "PK_credential" PRIMARY KEY ("SystemUserId")
);

CREATE TABLE auth.scope (
    "Id" citext NOT NULL,
    "ConcurrencyToken" citext NULL,
    "Description" citext NULL,
    "Descriptions" citext NULL,
    "DisplayName" citext NULL,
    "DisplayNames" citext NULL,
    "Name" citext NULL,
    "Properties" citext NULL,
    "Resources" citext NULL,
    CONSTRAINT "PK_scope" PRIMARY KEY ("Id")
);

CREATE TABLE auth.signing_key (
    "Id" uuid NOT NULL,
    "Use" citext NOT NULL,
    "Pfx" bytea NOT NULL,
    "CreatedOnUtc" timestamptz NOT NULL,
    CONSTRAINT "PK_signing_key" PRIMARY KEY ("Id")
);

CREATE TABLE auth."authorization" (
    "Id" citext NOT NULL,
    "ApplicationId" citext NULL,
    "ConcurrencyToken" citext NULL,
    "CreationDate" timestamptz NULL,
    "Properties" citext NULL,
    "Scopes" citext NULL,
    "Status" citext NULL,
    "Subject" citext NULL,
    "Type" citext NULL,
    CONSTRAINT "PK_authorization" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_authorization_application_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES auth.application ("Id")
);

CREATE TABLE auth.token (
    "Id" citext NOT NULL,
    "ApplicationId" citext NULL,
    "AuthorizationId" citext NULL,
    "ConcurrencyToken" citext NULL,
    "CreationDate" timestamptz NULL,
    "ExpirationDate" timestamptz NULL,
    "Payload" citext NULL,
    "Properties" citext NULL,
    "RedemptionDate" timestamptz NULL,
    "ReferenceId" citext NULL,
    "Status" citext NULL,
    "Subject" citext NULL,
    "Type" citext NULL,
    CONSTRAINT "PK_token" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_token_application_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES auth.application ("Id"),
    CONSTRAINT "FK_token_authorization_AuthorizationId" FOREIGN KEY ("AuthorizationId") REFERENCES auth."authorization" ("Id")
);

CREATE UNIQUE INDEX "IX_application_ClientId" ON auth.application ("ClientId") WHERE "ClientId" IS NOT NULL;
CREATE INDEX "IX_authorization_ApplicationId_Status_Subject_Type" ON auth."authorization" ("ApplicationId", "Status", "Subject", "Type");
CREATE UNIQUE INDEX "IX_credential_UserName" ON auth.credential ("UserName");
CREATE UNIQUE INDEX "IX_scope_Name" ON auth.scope ("Name") WHERE "Name" IS NOT NULL;
CREATE UNIQUE INDEX "IX_signing_key_Use" ON auth.signing_key ("Use");
CREATE INDEX "IX_token_ApplicationId_Status_Subject_Type" ON auth.token ("ApplicationId", "Status", "Subject", "Type");
CREATE INDEX "IX_token_AuthorizationId" ON auth.token ("AuthorizationId");
CREATE UNIQUE INDEX "IX_token_ReferenceId" ON auth.token ("ReferenceId") WHERE "ReferenceId" IS NOT NULL;

-- 0011 share object type: nothing here. It changes an index of data.principalobjectaccess, which the security bootstrap creates
-- with the new index on a new organization (SecurityBootstrap.IndexStatements); no PostgreSQL organization has the old one.

-- 0012 service endpoint secrets ----------------------------------------------------------------------------------------------

CREATE TABLE meta.serviceendpoint_secret (
    serviceendpointid uuid   NOT NULL CONSTRAINT "PK_meta_serviceendpoint_secret" PRIMARY KEY,
    authvalue         citext NOT NULL
);

-- 0013 workflow waits --------------------------------------------------------------------------------------------------------

CREATE TABLE meta.workflow_wait (
    asyncoperationid uuid        NOT NULL CONSTRAINT "PK_meta_workflow_wait" PRIMARY KEY,
    entity           citext      NOT NULL,
    objectid         uuid        NOT NULL,
    wake_at          timestamptz NOT NULL,
    seen_version     bigint      NULL
);

CREATE INDEX "IX_workflow_wait_record" ON meta.workflow_wait (entity, objectid);

-- 0014 custom control files --------------------------------------------------------------------------------------------------

CREATE TABLE meta.customcontrol_file (
    customcontrolid uuid   NOT NULL,
    path            citext NOT NULL,
    content         bytea  NOT NULL,
    sha256          citext NOT NULL,
    CONSTRAINT "PK_meta_customcontrol_file" PRIMARY KEY (customcontrolid, path)
);

-- 0015 migration runs --------------------------------------------------------------------------------------------------------

CREATE TABLE meta.migration_package (
    packageid uuid        NOT NULL CONSTRAINT "PK_meta_migration_package" PRIMARY KEY,
    state     citext      NOT NULL,
    bytes     bigint      NULL,
    sha256    citext      NULL,
    createdby uuid        NOT NULL,
    createdon timestamptz NOT NULL
);

CREATE TABLE meta.migration_run (
    runid            uuid        NOT NULL CONSTRAINT "PK_meta_migration_run" PRIMARY KEY,
    packageid        uuid        NULL,
    serverpath       citext      NULL,
    state            citext      NOT NULL,
    phase            integer     NOT NULL DEFAULT 0,
    options          citext      NOT NULL,
    asyncoperationid uuid        NULL,
    message          citext      NULL,
    createdby        uuid        NOT NULL,
    createdon        timestamptz NOT NULL,
    startedon        timestamptz NULL,
    endedon          timestamptz NULL,
    finalizedon      timestamptz NULL,
    -- SQL Server: isopen AS CAST(1 AS bit) PERSISTED, the key of the filtered unique index below.
    isopen           boolean     GENERATED ALWAYS AS (true) STORED
);

CREATE UNIQUE INDEX "UX_meta_migration_run_open" ON meta.migration_run (isopen) WHERE state <> 'finalized';

CREATE TABLE meta.migration_chunk (
    runid       uuid        NOT NULL,
    entry       citext      NOT NULL,
    phase       integer     NOT NULL,
    rows        integer     NOT NULL,
    failedrows  integer     NOT NULL,
    committedon timestamptz NOT NULL,
    CONSTRAINT "PK_meta_migration_chunk" PRIMARY KEY (runid, entry)
);

-- 0016 migration import ------------------------------------------------------------------------------------------------------

CREATE TABLE meta.migration_idmap (
    runid    uuid NOT NULL,
    sourceid uuid NOT NULL,
    targetid uuid NOT NULL,
    CONSTRAINT "PK_meta_migration_idmap" PRIMARY KEY (runid, sourceid)
);

CREATE TABLE meta.migration_deferred (
    seq        bigint      GENERATED ALWAYS AS IDENTITY,
    runid      uuid        NOT NULL,
    tablename  citext      NOT NULL,
    recordid   uuid        NOT NULL,
    columnname citext      NOT NULL,
    reftable   citext      NOT NULL,
    refid      uuid        NOT NULL,
    modifiedon timestamptz NULL,
    CONSTRAINT "PK_meta_migration_deferred" PRIMARY KEY (runid, tablename, recordid, columnname)
);

CREATE TABLE meta.migration_issue (
    issueid    bigint  GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_meta_migration_issue" PRIMARY KEY,
    runid      uuid    NOT NULL,
    phase      integer NOT NULL,
    severity   citext  NOT NULL,
    code       citext  NOT NULL,
    tablename  citext  NULL,
    recordid   uuid    NULL,
    columnname citext  NULL,
    message    citext  NOT NULL
);

CREATE INDEX "IX_meta_migration_issue_run" ON meta.migration_issue (runid, severity);
CREATE INDEX "IX_meta_migration_issue_record" ON meta.migration_issue (runid, code, recordid);

CREATE TABLE meta.migration_phase (
    runid     uuid        NOT NULL,
    phase     integer     NOT NULL,
    name      citext      NOT NULL,
    startedon timestamptz NOT NULL,
    endedon   timestamptz NULL,
    CONSTRAINT "PK_meta_migration_phase" PRIMARY KEY (runid, phase)
);

CREATE TABLE meta.migration_table (
    runid     uuid   NOT NULL,
    tablename citext NOT NULL,
    exported  bigint NOT NULL,
    CONSTRAINT "PK_meta_migration_table" PRIMARY KEY (runid, tablename)
);

-- 0017 and 0018 migration activation -----------------------------------------------------------------------------------------

CREATE TABLE meta.migration_deactivated (
    runid        uuid        NOT NULL,
    tablename    citext      NOT NULL,
    id           uuid        NOT NULL,
    name         citext      NOT NULL,
    sourcestate  integer     NOT NULL,
    sourcestatus integer     NOT NULL,
    -- 0018
    activatedon  timestamptz NULL,
    CONSTRAINT "PK_meta_migration_deactivated" PRIMARY KEY (runid, tablename, id)
);

-- The versions this baseline stands for. --------------------------------------------------------------------------------------

INSERT INTO meta.schema_version (version, applied_on) SELECT v, statement_timestamp() FROM generate_series(1, 18) AS v;

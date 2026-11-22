-- File and image columns (plan 8, rulings F6 and F8).
-- meta.filesession: the upload and download sessions of the file messages, kept in the database so that they survive a
-- restart; a session expires 24 hours after its last use and the cleanup sweep deletes it with its staged blocks.

CREATE TABLE meta.filesession (
    id                     uniqueidentifier NOT NULL CONSTRAINT PK_meta_filesession PRIMARY KEY,
    kind                   nvarchar(16)     NOT NULL CONSTRAINT CK_meta_filesession_kind CHECK (kind IN (N'upload', N'download')),
    caller_id              uniqueidentifier NOT NULL,
    entity_logical_name    nvarchar(128)    NOT NULL,
    record_id              uniqueidentifier NOT NULL,
    attribute_logical_name nvarchar(128)    NOT NULL,
    file_name              nvarchar(max)    NULL,
    created_on             datetime2        NOT NULL,
    last_used_on           datetime2        NOT NULL,
    fileattachment_id      uniqueidentifier NULL,
    block_ids              nvarchar(max)    NOT NULL
);
GO

CREATE INDEX IX_meta_filesession_last_used_on ON meta.filesession (last_used_on);
GO

-- The secret the continuation tokens are derived from (with the organization id): random, written once, never sent.
CREATE TABLE meta.filesecret (
    secret varbinary(64) NOT NULL
);
GO

INSERT INTO meta.filesecret (secret) VALUES (CRYPT_GEN_RANDOM(32));
GO

-- Blobs found by the cleanup sweep without a fileattachment row, and staging prefixes without a session row: deleted when
-- still orphaned 24 hours after first seen (the rows of a transaction in progress are not visible to the sweep).
CREATE TABLE meta.fileorphan (
    blob_key   nvarchar(450) NOT NULL CONSTRAINT PK_meta_fileorphan PRIMARY KEY,
    first_seen datetime2     NOT NULL
);
GO

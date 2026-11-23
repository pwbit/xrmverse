-- Incoming email (plan 4.1c). meta.mail_message holds the organization-wide key of every mail message the server sent or imported
-- (SHA-256 of its normalized Message-ID, or of its Date, From, To, Subject and body length when it has none), so that a message is
-- imported once whatever mailbox it reaches, and a copy of a sent email is never imported. The rows go with their email. A data table
-- is created by metadata, so a unique index on email cannot be a migration: the key lives here.
-- meta.mailbox_sync holds the synchronization state of a mailbox: the provider's cursor, the key of the message that failed last
-- with its count of failures, the keys skipped since the cursor last moved (a JSON array of hex keys), how many passes in a row that
-- message was refused for a missing privilege, and the generation of the state: a reset of the mailbox's incoming settings adds one, so
-- a pass that read an older generation does not write its stale cursor back.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.mail_message', N'U') IS NULL
    CREATE TABLE meta.mail_message (
        messagekey binary(32)        NOT NULL,
        emailid    uniqueidentifier  NOT NULL,
        incoming   bit               NOT NULL,
        createdon  datetime2         NULL,
        CONSTRAINT PK_meta_mail_message PRIMARY KEY (messagekey)
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'meta.mail_message') AND name = N'IX_meta_mail_message_emailid')
    CREATE INDEX IX_meta_mail_message_emailid ON meta.mail_message (emailid);
GO

IF OBJECT_ID(N'meta.mailbox_sync', N'U') IS NULL
    CREATE TABLE meta.mailbox_sync (
        mailboxid  uniqueidentifier  NOT NULL,
        [cursor]   nvarchar(max)     NULL,
        failkey    binary(32)        NULL,
        failcount  int               NOT NULL CONSTRAINT DF_meta_mailbox_sync_failcount DEFAULT 0,
        skipped    nvarchar(max)     NULL,
        deniedcount int              NOT NULL CONSTRAINT DF_meta_mailbox_sync_deniedcount DEFAULT 0,
        generation int               NOT NULL CONSTRAINT DF_meta_mailbox_sync_generation DEFAULT 0,
        updatedon  datetime2         NULL,
        CONSTRAINT PK_meta_mailbox_sync PRIMARY KEY (mailboxid)
    );
GO

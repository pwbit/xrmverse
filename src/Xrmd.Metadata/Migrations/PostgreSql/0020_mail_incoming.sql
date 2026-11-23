-- Incoming email (plan 4.1c): see the SQL Server migration of the same name. The key of every mail message sent or imported, and the
-- synchronization state of a mailbox. datetime2 is timestamptz here, as in the baseline.

CREATE TABLE IF NOT EXISTS meta.mail_message (
    messagekey bytea       NOT NULL,
    emailid    uuid        NOT NULL,
    incoming   boolean     NOT NULL,
    createdon  timestamptz NULL,
    CONSTRAINT "PK_meta_mail_message" PRIMARY KEY (messagekey)
);

CREATE INDEX IF NOT EXISTS "IX_meta_mail_message_emailid" ON meta.mail_message (emailid);

CREATE TABLE IF NOT EXISTS meta.mailbox_sync (
    mailboxid  uuid        NOT NULL,
    "cursor"   text        NULL,
    failkey    bytea       NULL,
    failcount  int         NOT NULL DEFAULT 0,
    skipped    text        NULL,
    deniedcount int        NOT NULL DEFAULT 0,
    generation int         NOT NULL DEFAULT 0,
    updatedon  timestamptz NULL,
    CONSTRAINT "PK_meta_mailbox_sync" PRIMARY KEY (mailboxid)
);

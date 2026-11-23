-- Mail secrets (plan 4.1b): the write-only passwords of an emailserverprofile (incomingpassword, outgoingpassword,
-- oauthclientsecret) and of a mailbox (password) live here, outside the data tables, so that no read path (Retrieve, queries,
-- FetchXML, TDS, change tracking, the Web API, audit) can return them. One row per record and secret; the rows go with the record.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.mail_secret', N'U') IS NULL
    CREATE TABLE meta.mail_secret (
        recordid uniqueidentifier NOT NULL,
        name     varchar(64)      NOT NULL,
        value    nvarchar(2048)   NOT NULL,
        CONSTRAINT PK_meta_mail_secret PRIMARY KEY (recordid, name)
    );
GO

-- Mail secrets (plan 4.1b): see the SQL Server migration of the same name. One row per record and secret, outside the data tables.

CREATE TABLE IF NOT EXISTS meta.mail_secret (
    recordid uuid          NOT NULL,
    name     varchar(64)   NOT NULL,
    value    varchar(2048) NOT NULL,
    CONSTRAINT "PK_meta_mail_secret" PRIMARY KEY (recordid, name)
);

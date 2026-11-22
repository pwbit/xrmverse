-- Custom control files (plan 3.5 UI-1d, ruling R2): the files a code component's manifest names (bundle, styles, resource strings,
-- images), kept byte for byte as the solution gives them, one row per control and path (the path relative to the manifest folder,
-- as the manifest writes it), with the SHA-256 of the content (lowercase hex) that the file route sends as its ETag. The rows go with
-- their customcontrol row. Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.customcontrol_file', N'U') IS NULL
    CREATE TABLE meta.customcontrol_file (
        customcontrolid uniqueidentifier NOT NULL,
        path            nvarchar(400)    NOT NULL,
        content         varbinary(max)   NOT NULL,
        sha256          char(64)         NOT NULL,
        CONSTRAINT PK_meta_customcontrol_file PRIMARY KEY (customcontrolid, path)
    );
GO

-- Local identity provider (plan 2.8): the auth schema of the organization database. Generated from AuthDbContext
-- (Database.GenerateCreateScript) and pinned to that model by AuthSchemaTests.Migration_matches_the_ef_model.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

GO

IF SCHEMA_ID(N'auth') IS NULL EXEC(N'CREATE SCHEMA [auth];');
GO

IF OBJECT_ID(N'[auth].[application]', N'U') IS NULL
    CREATE TABLE [auth].[application] (
        [Id] nvarchar(450) NOT NULL,
        [ApplicationType] nvarchar(50) NULL,
        [ClientId] nvarchar(100) NULL,
        [ClientSecret] nvarchar(max) NULL,
        [ClientType] nvarchar(50) NULL,
        [ConcurrencyToken] nvarchar(50) NULL,
        [ConsentType] nvarchar(50) NULL,
        [DisplayName] nvarchar(max) NULL,
        [DisplayNames] nvarchar(max) NULL,
        [JsonWebKeySet] nvarchar(max) NULL,
        [Permissions] nvarchar(max) NULL,
        [PostLogoutRedirectUris] nvarchar(max) NULL,
        [Properties] nvarchar(max) NULL,
        [RedirectUris] nvarchar(max) NULL,
        [Requirements] nvarchar(max) NULL,
        [Settings] nvarchar(max) NULL,
        CONSTRAINT [PK_application] PRIMARY KEY ([Id])
    );
GO

IF OBJECT_ID(N'[auth].[credential]', N'U') IS NULL
    CREATE TABLE [auth].[credential] (
        [SystemUserId] uniqueidentifier NOT NULL,
        [UserName] nvarchar(256) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [TotpKey] varbinary(64) NULL,
        [TotpLastStep] bigint NULL,
        [FailedCount] int NOT NULL,
        [LockedUntilUtc] datetime2 NULL,
        [ChangedOnUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_credential] PRIMARY KEY ([SystemUserId])
    );
GO

IF OBJECT_ID(N'[auth].[scope]', N'U') IS NULL
    CREATE TABLE [auth].[scope] (
        [Id] nvarchar(450) NOT NULL,
        [ConcurrencyToken] nvarchar(50) NULL,
        [Description] nvarchar(max) NULL,
        [Descriptions] nvarchar(max) NULL,
        [DisplayName] nvarchar(max) NULL,
        [DisplayNames] nvarchar(max) NULL,
        [Name] nvarchar(200) NULL,
        [Properties] nvarchar(max) NULL,
        [Resources] nvarchar(max) NULL,
        CONSTRAINT [PK_scope] PRIMARY KEY ([Id])
    );
GO

IF OBJECT_ID(N'[auth].[signing_key]', N'U') IS NULL
    CREATE TABLE [auth].[signing_key] (
        [Id] uniqueidentifier NOT NULL,
        [Use] varchar(3) NOT NULL,
        [Pfx] varbinary(max) NOT NULL,
        [CreatedOnUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_signing_key] PRIMARY KEY ([Id])
    );
GO

IF OBJECT_ID(N'[auth].[authorization]', N'U') IS NULL
    CREATE TABLE [auth].[authorization] (
        [Id] nvarchar(450) NOT NULL,
        [ApplicationId] nvarchar(450) NULL,
        [ConcurrencyToken] nvarchar(50) NULL,
        [CreationDate] datetime2 NULL,
        [Properties] nvarchar(max) NULL,
        [Scopes] nvarchar(max) NULL,
        [Status] nvarchar(50) NULL,
        [Subject] nvarchar(400) NULL,
        [Type] nvarchar(50) NULL,
        CONSTRAINT [PK_authorization] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_authorization_application_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [auth].[application] ([Id])
    );
GO

IF OBJECT_ID(N'[auth].[token]', N'U') IS NULL
    CREATE TABLE [auth].[token] (
        [Id] nvarchar(450) NOT NULL,
        [ApplicationId] nvarchar(450) NULL,
        [AuthorizationId] nvarchar(450) NULL,
        [ConcurrencyToken] nvarchar(50) NULL,
        [CreationDate] datetime2 NULL,
        [ExpirationDate] datetime2 NULL,
        [Payload] nvarchar(max) NULL,
        [Properties] nvarchar(max) NULL,
        [RedemptionDate] datetime2 NULL,
        [ReferenceId] nvarchar(100) NULL,
        [Status] nvarchar(50) NULL,
        [Subject] nvarchar(400) NULL,
        [Type] nvarchar(150) NULL,
        CONSTRAINT [PK_token] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_token_application_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [auth].[application] ([Id]),
        CONSTRAINT [FK_token_authorization_AuthorizationId] FOREIGN KEY ([AuthorizationId]) REFERENCES [auth].[authorization] ([Id])
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[application]') AND name = N'IX_application_ClientId')
    CREATE UNIQUE INDEX [IX_application_ClientId] ON [auth].[application] ([ClientId]) WHERE [ClientId] IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[authorization]') AND name = N'IX_authorization_ApplicationId_Status_Subject_Type')
    CREATE INDEX [IX_authorization_ApplicationId_Status_Subject_Type] ON [auth].[authorization] ([ApplicationId], [Status], [Subject], [Type]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[credential]') AND name = N'IX_credential_UserName')
    CREATE UNIQUE INDEX [IX_credential_UserName] ON [auth].[credential] ([UserName]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[scope]') AND name = N'IX_scope_Name')
    CREATE UNIQUE INDEX [IX_scope_Name] ON [auth].[scope] ([Name]) WHERE [Name] IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[signing_key]') AND name = N'IX_signing_key_Use')
    CREATE UNIQUE INDEX [IX_signing_key_Use] ON [auth].[signing_key] ([Use]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[token]') AND name = N'IX_token_ApplicationId_Status_Subject_Type')
    CREATE INDEX [IX_token_ApplicationId_Status_Subject_Type] ON [auth].[token] ([ApplicationId], [Status], [Subject], [Type]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[token]') AND name = N'IX_token_AuthorizationId')
    CREATE INDEX [IX_token_AuthorizationId] ON [auth].[token] ([AuthorizationId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[auth].[token]') AND name = N'IX_token_ReferenceId')
    CREATE UNIQUE INDEX [IX_token_ReferenceId] ON [auth].[token] ([ReferenceId]) WHERE [ReferenceId] IS NOT NULL;
GO

# Local identity provider

[← xrmverse documentation](../README.md#documentation)

## Introduction

Local mode lets an xrmverse environment sign people and services in with its own identity provider (IdP). No Microsoft Entra
ID tenant is needed, and nothing leaves your network to sign in. This page is the reference for operating it.

### Compared with Dataverse

| Area | xrmverse Local mode | Compared with Dataverse |
|---|---|---|
| Identity provider | The server's own IdP, endpoints under `/adfs/*` | **Extra**: Dataverse signs in with Entra ID only |
| Flows | Client credentials, authorization code with PKCE, device code, refresh tokens | Covers what `ServiceClient` and the xrmverse tools use |
| Multi-factor | Password plus optional TOTP | **Narrower** than Entra policies (no conditional access) |
| Application users | A confidential client bound to any enabled `systemuser` you choose | **Differs**: there is no `systemuser.applicationid` |
| Power Platform CLI (`pac`) | Not supported | **Narrower**: `pac` hard-codes Microsoft's authorities |
| Transport | HTTPS only | Same as the cloud service |

### Tasks at a glance

| Task | Command or place | Chapter |
|---|---|---|
| Create an environment in Local mode | `xrmd server init --auth local` | [Create an environment](#create-an-environment) |
| Give a user a password, TOTP | `xrmd idp user set-password`, `xrmd idp user totp enable` | [Users and TOTP](#users-and-totp) |
| Let a service sign in | `xrmd idp client add --app-user` | [Services](#services) |
| Connect from .NET | `ServiceClient` connection strings | [Connect with ServiceClient](#connect-with-serviceclient) |
| Move to Entra later | Remap `azureactivedirectoryobjectid` | [Moving from Local to Entra](#moving-from-local-to-entra) |

## When to choose Local

Choose Local when:

- the environment has no Internet access, or must work with the Internet blocked;
- you have no Entra tenant, or do not want to register applications in it;
- you want a quick install for a team, a lab or a CI environment.

Choose Entra ([Installing xrmverse](install.md#4-entra-app-registrations)) when you already manage users there and want single sign-on, conditional access or multi-factor policies beyond TOTP.

### How it works

Local mode serves the OAuth 2.0 endpoints under `/adfs/*` of the environment's own host. They follow the layout of Active
Directory Federation Services, because that is what `ServiceClient` expects from an on-premises server.

| Flow | Used by |
|---|---|
| Client credentials | Services |
| Authorization code with PKCE | Browsers, the web admin, the CLI |
| Device code | The CLI on a machine without a browser |
| Refresh tokens | All of the above |

Users have a password and optionally a TOTP code. There is no consent screen: the clients are first-party.

## Create an environment

```bash
xrmd server init --dir /srv/xrmd --org acme --domain example.com --tls acme --acme-email admin@example.com \
  --auth local --accept-microsoft-license --accept-sql-server-eula
```

`--auth local` needs an `https://` host for every organization ([HTTPS only](#https-only)). `init` generates the administrator and an initial password, and prints the password once:

```text
Initial admin password for acme (shown once): ...
```

### The initial administrator password

The password is also written to `<dir>/secrets/<org>/admin-password`. Every organization has its own folder, and its container mounts only that folder, so one organization cannot read another's password. Neither `secrets` nor the organization folder can be accessed by other users (both are created 0700 for you; the container setup then gives each organization folder to the server's group, mode 2770, with no access for others). `xrmd server org remove` deletes the organization's folder. The server reads the file at its first start, sets the password and deletes the file. After that the password exists only in the database, as a hash. The administrator's user name is the one `init` printed: `Administrator`, or the `SEED_ADMIN_NAME` of `.env`.

When you re-run `init` for an organization that already has its administrator, the administrator and credential are kept and no new password is made. To set one, use `xrmd idp user set-password`.

### First sign-in

Sign in to the web admin at `https://<host>/manage/` with that user name and password. Sign in with the CLI:

```bash
xrmd auth login --profile acme --url https://acme.example.com/ --mode device-code --idp local
```

`--idp local` uses the server's own IdP: the authority is `<url>adfs/` and the client id defaults to `xrmd-cli`. `init` prints this command for each organization. With the proxy's internal CA, the command also carries `--ca-file <dir>/certs/caddy-root.crt`, so that the CLI trusts that host only. Later logins of the same profile keep the CA file. Then run `xrmd auth status` to see the profile.

## Users and TOTP

A user is a normal `systemuser` row. It can sign in once it has a local credential. Set or change the password (at least 12 characters, read from standard input, never from the command line):

```bash
printf '%s\n' 'a long passphrase' | xrmd idp user set-password kim@example.com --password-stdin
```

The user is given as a domain name, a `systemuserid` or an object id.

Turn TOTP on, so that sign-in needs a code from an authenticator app:

```bash
xrmd idp user totp enable kim@example.com    # prints an otpauth:// URI once
xrmd idp user totp disable kim@example.com
xrmd idp user remove kim@example.com         # removes the credential, not the user
```

Add the `otpauth://` URI to the app (paste it, or turn it into a QR code with any tool).

### What each change does

| Change | Effect |
|---|---|
| Set a password | Writes a new GUID to `azureactivedirectoryobjectid` when the user has none (the `oid` of the user's tokens); ends the user's sessions and refresh tokens; clears a lockout |
| Enable TOTP | Ends the user's refresh tokens |
| Five failed sign-in attempts | The account is locked for 15 minutes; the sign-in page never says which part was wrong |
| Disable the user (`isdisabled`) or remove the credential | No new tokens and no refresh from now on; access tokens already issued stay valid until they expire |

## Services

A service (a script, an integration, a CI job) signs in with a client id and a secret, and acts as one user.

```bash
xrmd idp client add --name "nightly import" --app-user svc-import@example.com
```

The command prints the client id and the secret once. Store the secret now: it is not shown again, and not listed later. `xrmd idp client list` shows the clients without secrets, `xrmd idp client remove <client id>` deletes one.

Notes:

- xrmverse has no `systemuser.applicationid`. A confidential client is bound to **any enabled `systemuser` that you choose**. Create a dedicated user for each service, give it the roles the service needs, and use it with `--app-user`. Do not bind a service to a person.
- The tokens of the client carry that user's identity, so `WhoAmI` returns the user bound to the client.
- If the bound user is disabled or deleted, the client gets `invalid_grant`.
- A client-credentials request without `scope` is accepted: the token has `aud` set to the environment's audience and no `scp` claim (no scope was granted). A scope for another resource is refused with `invalid_scope`.
- The built-in clients `xrmd-cli` and `xrmd-web` are public, are recreated at every start and cannot be removed.

For a desktop application that signs a person in with a browser, make a public client:

```bash
xrmd idp client add --name "my tool" --public --redirect-uri http://localhost
```

## Connect with ServiceClient

Use the environment's URL with the audience as the host. `ServiceClient` drops the port of the URL, so Local mode listens on 443.

Service (client credentials):

```csharp
var client = new ServiceClient(
    "AuthType=ClientSecret;Url=https://acme.example.com;ClientId=<client id>;ClientSecret=<secret>");
```

Person, interactive, with a public client (the browser opens the local sign-in page):

```csharp
var client = new ServiceClient(
    "AuthType=OAuth;Url=https://acme.example.com;Username=kim@example.com;" +
    "ClientId=<public client id>;RedirectUri=http://localhost;LoginPrompt=Auto");
```

With your own token source (for example MSAL.NET and the device-code flow against `https://acme.example.com/adfs/`):

```csharp
var client = new ServiceClient(new Uri("https://acme.example.com"), async resource => await GetTokenAsync(resource));
```

The first two connection strings are what `ServiceClient` supports for on-premises servers with ADFS. The acceptance tests `ServiceClient_client_secret_works_without_entra` and `ServiceClient_token_provider_works` run the first and third form against a real host (see `tests/Xrmd.Integration.Tests/LocalIdpServiceClientTests.cs`). The interactive `AuthType=OAuth` form is checked by hand.

## HTTPS only

Local mode requires `Auth:Audience` with `https://`. The host refuses to start otherwise, and `server init --auth local` refuses an organization without an `https` URL. There is no HTTP exception: MSAL, which `ServiceClient` uses, rejects authorities that are not HTTPS. Use the production install with certificates ([Certificates](install.md#6-certificates)).

Clients must trust the certificate. With Caddy's internal CA, trust `caddy-root.crt` as described there.

### Behind a reverse proxy

The IdP builds its issuer and URLs from the configured audience. A proxy in front of xrmverse must forward `Host` and `X-Forwarded-Proto` (Caddy does by default, and the generated Caddyfile is correct). If they do not match the audience, you get 404 on `/adfs/*` or issuer errors in the clients. See [Your own reverse proxy](install.md#7-your-own-reverse-proxy).

## Sessions and limits

- The IdP's own login cookie is protected with ASP.NET Core Data Protection keys. These keys are **not persisted**: a server restart signs people out of the IdP login pages only. Access and refresh tokens stay valid, so a client that has a refresh token does not notice. Persisting the keys is on the backlog.
- `prompt=login`, `max_age` and consent prompts are ignored. There is no consent screen, because only first-party clients exist.
- Access tokens are signed JWTs and are not encrypted; codes and refresh tokens are.
- Key rotation is not available yet (backlog B1). The signing key is created at the first start and used until then.

## pac is not supported

The Power Platform CLI (`pac auth create --url https://<host>`) does **not** work in Local mode: `pac` has Microsoft's authorities hard-coded and cannot use another identity provider. To move solutions use `xrmd solution import` and `xrmd solution export` (see [Solutions](solutions.md)). With Entra mode `pac` works.

## Where the keys are

Each organization has its own key ring: one RSA 2048 signing certificate and one encryption certificate, created at the first start in Local mode. They are stored in the organization's database (SQL Server or PostgreSQL), in the `auth` schema, as PFX bytes. Passwords are stored as hashes there too.

So **a database backup contains the signing keys**, and whoever can read the backup can forge tokens for that organization. Protect the backups as you protect the database. The database already holds all the data of the organization, so it is the trust boundary.

A restore brings the keys back with the data, so tokens issued before the backup stay valid.

## Moving from Local to Entra

The token's `oid` is the user's `systemuser.azureactivedirectoryobjectid`. In Local mode, creating a credential for a user without one writes a new GUID there. An Entra token carries the Entra object id instead, so after you switch the organization to `--auth entra` each user must have the `azureactivedirectoryobjectid` of the matching Entra account. Remap the column before the switch, user by user (with a SQL `UPDATE` of `data.systemuser`, or an update of the `systemuser` row through the Web API), and set the administrator's Entra object id: `--admin-object-id` of `server init`, or `ORG_<ORG>_ADMIN_OBJECT_ID` in `.env` for an installation made by `server init` (`SEED_ADMIN_OBJECT_ID` is the key of the single-organization `docker/.env`). Services that used a client secret need an Entra app registration and an application user. The local credentials and clients stay in the `auth` schema and are ignored.

## See also

- [Installing xrmverse](install.md)
- [Security](security.md)
- [SQL endpoint](sql-endpoint.md): available with Local mode only
- [CLI xrmd](cli.md)

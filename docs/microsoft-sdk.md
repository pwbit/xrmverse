# Installing the Microsoft SDK

[← xrmverse documentation](../README.md#documentation)

## Introduction

The Microsoft SDK is the set of assemblies that plug-ins, clients and the SOAP endpoint are built on. It is not open source,
so **xrmverse does not ship it**: no image, release archive or package contains it. You download it yourself, once, after
accepting Microsoft's license terms ("bring your own SDK"). The `xrmd sdk` commands do the download, check it and record
your acceptance.

| Package | Assemblies taken | Needed for |
|---|---|---|
| `Microsoft.PowerPlatform.Dataverse.Client` **1.2.27** (the only version supported) | `Microsoft.Xrm.Sdk`, `Microsoft.Crm.Sdk.Proxy`, `Microsoft.PowerPlatform.Dataverse.Client`, from `lib/net10.0/` | The server and most CLI commands: **required** |
| `Microsoft.CrmSdk.Workflow` **9.0.2.60** | `lib/net462/Microsoft.Xrm.Sdk.Workflow.dll` | Only [custom workflow activities](workflows.md#custom-workflow-activities): optional |

The .NET runtime and the MIT/Apache-licensed packages that xrmverse does ship are not affected.

### Compared with Dataverse

In Dataverse the SDK is part of the service. In xrmverse the server refuses to start without it, and you choose when to
accept its license terms.

## Installing it

```bash
xrmd sdk install --dir ./xrmd-server     # prompts (default: no) with the license URL, then downloads from nuget.org
xrmd sdk status --dir ./xrmd-server      # version, path and acceptance record (-o json)
```

`xrmd server init` offers the same installation ("Install the Microsoft SDK now? [y/N]", default no; without a terminal it
only prints the command to run).

### Options

| Option | Effect |
|---|---|
| `--accept-microsoft-license` | Your explicit acceptance without the prompt; required when stdin is not a terminal (otherwise exit `2`). On `server init` it installs the SDK into `<dir>/sdk` as part of init |
| `--dir <installation>` | Installs into `<installation>/sdk`, which is mounted read-only into every organization's container at `/var/lib/xrmd/sdk` (`XRMD_SDK_DIR` in the image) |
| `--user` | Also (or, without `--dir`, only) installs into the user folder: `~/.xrmd/sdk`, or `XRMD_SDK_DIR` |
| `--from <file.nupkg>` | Offline: the package comes from a file; same hash check, same acceptance. Without `--from-workflow` the workflow SDK is not installed (a note on stderr says so, or says that it was removed from the folder) |
| `--from-workflow <file.nupkg>` | Offline: the workflow package `Microsoft.CrmSdk.Workflow` comes from a file, with its own hash check |

### What the installation does

1. Without a file option, `install` (and `server init`) asks for **one** acceptance, which names both packages.
2. Nothing is downloaded or extracted before the acceptance.
3. Both packages are downloaded and checked against their pinned SHA-512 before anything is written.
4. Only the assemblies in the table above are taken. The install fails, naming the path, if the main package has no
   `lib/net10.0/` folder.
5. A failure leaves the previous folder as it was.
6. `sdk.json` in the folder records the version, the hash, and when and how (`flag` or `prompt`) the terms were accepted.
   When the workflow SDK is installed it also records `workflowPackage`, `workflowVersion` and `workflowSha512` (records
   written without them stay valid).

SDK folders installed earlier from `lib/net8.0/` keep working on the .NET 10 host and CLI: they hold the same assembly names
and versions, so no reinstall is required.

## Where it is looked for

| Component | Search order |
|---|---|
| The server (host) | `Sdk:Path`, else `XRMD_SDK_DIR`, else `~/.xrmd/sdk` |
| The CLI | `XRMD_SDK_DIR`, else `~/.xrmd/sdk` |

The default `~/.xrmd/sdk` (`%USERPROFILE%\.xrmd\sdk` on Windows) is the same for both and does not move with
`XRMD_CONFIG_DIR`.

## What needs it

### The `server` commands

- `server up`, `server upgrade` and `server org add` refuse to start containers (exit 1, with the
  `xrmd sdk install --dir <dir>` command) while `<dir>/sdk/sdk.json` is missing.
- An old single-service directory is refused too, because its compose file does not mount the SDK folder: migrate it with
  `xrmd server init --force --dir <dir>`, then `xrmd sdk install --dir <dir>`.

### The server

- The host loads `Microsoft.Xrm.Sdk` and `Microsoft.Crm.Sdk.Proxy` from the folder at startup, before anything uses them,
  and hands that one copy to every plug-in.
- Without the SDK it exits with code 3 and the message `The Microsoft SDK is not installed (looked in <folder>)…`.
- With an SDK folder of another version (`sdk.json` or the `Microsoft.Xrm.Sdk` file version) it exits with code 3 naming
  both versions.
- In a container of an installation the message says `xrmd sdk install`: for an installation the command is
  `xrmd sdk install --dir <installation>`, run on the Docker host (it fills `<installation>/sdk`, the folder mounted at
  `/var/lib/xrmd/sdk`).

### The workflow SDK

- The host does **not** require it. When `Microsoft.Xrm.Sdk.Workflow.dll` is in the folder it loads it after
  `Microsoft.Xrm.Sdk`; a version or hash that differs from the pinned one is a warning, not a refusal.
- Without it, the host starts and every custom workflow activity step is an unsupported step naming `xrmd sdk install`
  (see [Classic workflows](workflows.md#custom-workflow-activities)).

### The CLI

| Commands | Need the SDK |
|---|---|
| `data`, `table`, `dev`, `apply`, … | Yes; without it they print the same message and exit 1 |
| `server`, `auth`, `sdk`, `plugin`, `solution`, `publisher`, `user`/`role`, `logs`, `mcp` | No |

`plugin register` reads the plug-in's `IPlugin` types with the SDK folder's `Microsoft.Xrm.Sdk`, or from metadata alone
when there is none.

## Checking it

| Check | Reports |
|---|---|
| `xrmd sdk status` | Version, path, acceptance record; `workflowSdk` (the package and version, or `missing (run 'xrmd sdk install')`), `workflowVersion`, `workflowSha512` |
| `GET /admin/health` | `sdkVersion`, and `workflowSdkVersion` (null without the workflow SDK) |
| `xrmd server doctor` (`GET /admin/doctor`) | `sdk` check: an error, naming `xrmd sdk install`, when the loaded SDK is not the pinned one. `sdk-workflow` check: a warning, naming the file, the folder and `xrmd sdk install`, when the workflow SDK is missing or is not the pinned one |

## See also

- [Getting started (Docker)](getting-started.md), [Installing xrmverse](install.md)
- [Plug-ins](plugins.md), [Classic workflows](workflows.md)

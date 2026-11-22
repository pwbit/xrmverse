# Files and images

[← xrmverse documentation](../README.md#documentation)

## Introduction

File and image columns behave as in Dataverse: the same column types (`FileAttributeMetadata`, `ImageAttributeMetadata`),
SDK messages, Web API routes, headers, limits and error codes. Their content is kept in a **file store**, never in SQL
tables. The one exception is the image thumbnail (see below).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Column types, messages, routes, headers, limits, error codes | As in Dataverse | Same |
| Where content lives | A file store you choose: file system, S3-compatible or Azure Blob | **Differs**: Dataverse manages Azure Blob for you |
| `DownloadBlock` size | At most 4 MB per block | **Narrower**: Dataverse publishes no limit |
| Image size | Decoded up to 64 Mi pixels | xrmverse bound |
| Single-property routes | File and image columns only | **Narrower** |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Choose and configure the store | `Files:Store` and its keys | [Stores and keys](#stores-and-keys) |
| Check the store works | `xrmd server doctor` | [Checking the file store](#checking-the-file-store) |
| Add a file or image column | Column metadata | [Columns](#columns) |
| Upload and download | SDK messages, Web API routes | [Messages and routes](#messages-and-routes) |
| Back up files | `org backup` | [Backup and restore](#backup-and-restore) |

## Stores and keys

| Key | Values |
|---|---|
| `Files:Store` | `FileSystem` (default), `S3Compatible` or `AzureBlob`; any other value makes startup fail |
| `Files:FileSystem:Root` | default `/var/lib/xrmd/files` |
| `Files:S3:ServiceUrl`, `Files:S3:Bucket`, `Files:S3:AccessKey`, `Files:S3:SecretKey` | required with `S3Compatible`: a missing one makes startup fail, naming it; `ServiceUrl` is an absolute http(s) URL and the bucket must exist |
| `Files:S3:Region`, `Files:S3:ForcePathStyle` | default `us-east-1` and `true` (what MinIO and most self-hosted stores need) |
| `Files:Azure:Container` | required with `AzureBlob`; the container must already exist |
| `Files:Azure:ConnectionString` or `Files:Azure:ServiceUri` | with `AzureBlob`, set exactly one: none or both makes startup fail |

- A file, or the full-size image of an image column, is the blob `{organization database}/{fileattachmentid}`. Staged
  upload blocks are `{organization database}/_upload/{sessionid}/{blockindex}`. Each organization's blobs therefore sit
  under its database name, in one store shared by every organization.
- **Docker.** The compose files (`docker/docker-compose.yml` and the one `xrmd server init` writes) mount a `files` named
  volume at `/var/lib/xrmd/files` and pass the `FILES_*` values of `.env` to the `Files__*` keys (empty = file-system
  store). The image creates that directory owned by the aspnet image's `APP_UID`, and xrmd runs as `1654:1654`. With
  `S3Compatible` or `AzureBlob` the volume is unused.
- Each file has a `fileattachment` row (`filename`, `mimetype`, `filesizeinbytes`, `objectid`, `regardingfieldname`). Those
  rows can be read only through Read on the owning record. They are never created, updated or deleted directly
  (`0x80040203`).

## Checking the file store

`xrmd server doctor` (`GET /admin/doctor`) has a `fileStore` check that confirms the configured store is usable without
writing any blob. It is `ok`, `warning` or `error`, and its detail names the store and its directory, bucket or container:

- **Local directory** (`Files:Store=FileSystem`, the default): the root must be writable by the xrmd process. The check
  briefly creates and removes a temporary file. A root that does not exist yet is a warning ("will be created on the first
  write") when the nearest existing parent directory is writable, since the store creates it on first use; it is an error when
  it cannot be created (no writable parent, or the path or a parent is a file). The check itself never creates the root.
- **S3-compatible**: the bucket is probed with a HEAD request; a missing bucket or a refusal (wrong keys, no permission) is an error.
- **Azure Blob**: the container is probed for existence; a missing container is an error (xrmd does not create it).

The check gives the store 10 seconds to answer; after that it is an error. An error message carries the store and the HTTP status with a short hint (for example access denied, wrong region or endpoint), never a connection string, key or SAS token.
The check is not part of `/healthz`, which stays a cheap liveness probe.

## Azure Blob Storage

With `Files:Store=AzureBlob` every blob is a block blob in one container of an Azure storage account.

- **The container must exist.** xrmd never creates it (that would need broader rights than reading and writing blobs).
  A missing container is not detected at startup or by `/healthz`, but `xrmd server doctor` reports it in its `fileStore`
  check (see [Checking the file store](#checking-the-file-store)); without that check the storage service's error
  (`ContainerNotFound`) surfaces on the first file operation (upload, download, backup, sweep) and in the logs. Deleting
  a single file from a missing container may not report an error (the delete is a no-op), while deleting by prefix does.
  A missing `Files:Azure:Container` stops startup with
  `Files:Store is 'AzureBlob' but the required setting Files:Azure:Container is missing.`
- **Two ways to sign in.** Set exactly one of:
  - `Files:Azure:ConnectionString`: an account-key or shared-access-signature connection string. This also covers a local
    Azurite emulator. It is a secret and is never written to a log or an error message.
  - `Files:Azure:ServiceUri`: the account's blob endpoint (for example `https://myaccount.blob.core.windows.net`). xrmd then
    signs in with Microsoft Entra ID through `DefaultAzureCredential`. Inside the container, a system-assigned managed
    identity of the host works as is; a user-assigned one needs `AZURE_CLIENT_ID` (forwarded by the shipped compose
    files, set it in `.env`); `az login` is only for running xrmd directly on a workstation; workload identity also
    needs `AZURE_TENANT_ID` and `AZURE_FEDERATED_TOKEN_FILE`, which the shipped compose files do not forward. The
    identity needs the role **Storage Blob Data Contributor**. Assigned at storage-account scope it already allows
    requesting a user delegation key, which lets the storage service copy blocks itself (next point). If Contributor is
    scoped to the container only, also assign **Storage Blob Delegator** at account scope for that. An invalid value stops startup with
    `Files:Azure:ServiceUri is not a valid absolute https URL.` Plain `http` is always refused, because the Azure SDK
    does not send Entra credentials over `http`; for local tests against Azurite, use a connection string instead.
- **Joining the parts of a chunked upload.** When a file is committed, its staged blocks are joined into one blob. Where
  it can, xrmd asks the storage service to copy each part itself, authorizing the source with a read-only signature valid
  for one hour (a service SAS with an account key, a user delegation SAS with Entra ID), so the data never passes through
  xrmd. When that is not possible (Contributor scoped to the container without Storage Blob Delegator at account scope, a SAS connection string without a signing key, an
  emulator that lacks the operation, or more than 50,000 parts), xrmd reads the parts and writes them back in 4 MiB blocks
  instead. Both ways commit one block list, so the target appears all at once or not at all.
- **Known limit.** Two writes to the same key at the same time never mix their content, but the slower one can fail with
  an `InvalidBlockList` error from the service. xrmd never does that: every key is written by one operation at a time.

## Columns

- A **file** column is a `uniqueidentifier` holding the `fileattachmentid`, plus the read-only companion `<column>_name`
  (the file name). It cannot be set with Create or Update: any value other than null is refused (`0x80040203`, "can only be
  set with the file upload messages"). Setting it to null on Update deletes the file, as `DeleteFile` does.
- An **image** column holds the thumbnail, plus the companions `<column>id` (the image id), `<column>_timestamp` (UTC ticks
  of the last change) and `<column>_url` (`/Image/download.aspx?Entity=…&Attribute=…&Id=…&Timestamp=…`, null when empty).
  Setting it to a `byte[]` on Create or Update stores the thumbnail and, when `CanStoreFullImage` is true, the full-size
  image in the file store; null deletes both.
- **Thumbnails are stored in the database** (`varbinary(max)` in the image column itself): the one deliberate exception to
  "no blobs in SQL". Dataverse returns the thumbnail inline on Retrieve and RetrieveMultiple, and reading it from the store
  would cost one store read per row; a thumbnail is at most 144×144 pixels, a few tens of KB. Moving thumbnails to the
  store later would be a data migration.
- Thumbnail rules (Dataverse's): when a side is over 144 pixels, the centre square on the shorter side resized to 144×144;
  otherwise the centre square on the shorter side, not resized. JPEG in, JPEG out; anything else, PNG. Image columns take
  GIF, JPEG, BMP and PNG; anything else, or an image that does not decode completely, is refused with `0x80072553`
  (ProcessImageFailure), never stored as is.
- **Primary image.** The first image column of a table is the primary one (`IsPrimaryImage`). As in Dataverse,
  `IsPrimaryImage = true` on a create is ignored when the table already has an image column; the flag moves only with an
  update of another column to true. Setting it to false on the primary column is refused (`0x80040203`). Deleting the primary column makes the
  oldest remaining image column primary. On Create, only the primary image column may be set (`0x80090487`).
- The first file or image column of a table creates the relationship `{table}_FileAttachments` (1:N to `fileattachment` on
  `objectid`); deleting the last one removes it. Companions are created and dropped with their main column; deleting that
  relationship or changing the `fileattachment.objectid` lookup directly is refused (`0x80040203`).
- **AllColumns** (SDK `ColumnSet(true)`, FetchXML `all-attributes`, a Web API read without `$select`) does not include image
  columns, as in Dataverse; their companions are included. Select an image column by name to get its thumbnail.

## Messages and routes

- SDK and Web API actions: `InitializeFileBlocksUpload`, `UploadBlock`, `CommitFileBlocksUpload`,
  `InitializeFileBlocksDownload`, `DownloadBlock`, `DeleteFile`. `IsChunkingSupported` is always true. On an image column the
  upload messages set the image, and the download messages read the full-size image (404 `0x80040217` when none was kept).
- Web API properties on `{entityset}({id})/{column}`:
  - `PATCH` with an `application/octet-stream` body uploads in one request (at most 128 MB, 413 beyond). The name is the
    `x-ms-file-name` header (ASCII only) or, for other characters, the `x-ms-file-name` query parameter. The MIME type is
    the `mimetype` header, otherwise the one of the file name's extension.
  - `PATCH` with `x-ms-transfer-mode: chunked` and no body starts a chunked upload: 200 with `Location` (`?sessiontoken=…`)
    and `x-ms-chunk-size: 4194304`. Each chunk is a `PATCH` on that `Location` with `Content-Range: bytes a-b/total`,
    in order: 206 until the last byte, then 204.
  - `GET …/$value` downloads with `x-ms-file-size`, `x-ms-file-name`, `mimetype` and `X-Content-Type-Options: nosniff`; a
    `Range: bytes=a-b` (or `bytes=a-`) gives a 206. On an image column it returns the thumbnail, and with `?size=full` the
    full-size image.
  - `GET` without `$value` returns the property (a file's id, an image's thumbnail as base64); `DELETE` clears the column.
- `GET /Image/download.aspx?Entity&Attribute&Id&Timestamp[&Full=true]` (the `<column>_url` of an image) is served by the
  host. It requires a token and Read on the record; `Id` is the image id. Its `Content-Type` is the one the image content
  shows (GIF, JPEG, BMP or PNG), whatever MIME type the upload named, with `X-Content-Type-Options: nosniff`.
- Every upload, download and delete goes through the pipeline: a Web API `PATCH` runs `InitializeFileBlocksUpload`,
  `UploadBlock` and `CommitFileBlocksUpload` (so plug-in steps on those messages fire, as does the `Update` of the record
  the commit makes); `DELETE` runs an `Update` of the column to null. A `GET …/$value` of a file or full image (and
  `/Image/download.aspx?…&Full=true`) runs `InitializeFileBlocksDownload` and one `DownloadBlock` per 4 MB, streamed to
  the response (a `Range` is the offset and length of those blocks); a thumbnail is a `Retrieve` of the image column.
- The CLI (`table column add --type file|image`, schema files) and solutions create these columns: see
  [Tables, plug-ins, data and schema files](cli.md#tables-plug-ins-data-and-schema-files). The web admin shows a file or image
  column on a record's form, with upload, download and delete; on the create form these editors are disabled until the
  record exists.

## Limits

Dataverse's values:

| Limit | Value |
|---|---|
| Upload block, chunk (`x-ms-chunk-size`) | 4 MB (4194304 bytes) |
| Single-request Web API upload | 128 MB |
| File `MaxSizeInKB` | default 32768, range 1 to 10485760 |
| Image `MaxSizeInKB` | default 10240, range 1 to 30720 |
| Thumbnail (`MaxHeight`, `MaxWidth`) | 144×144, read-only |

A file over `MaxSizeInKB` is refused by the block that crosses it, not only at commit (`0x80044a02` "Attachment file size
is too big."; an image `0x80072553`), and its staged blocks are deleted.

## Security and sessions

- Upload and delete require **Write** on the record, download **Read**.
- A continuation token (and the Web API `sessiontoken`) is opaque and bound to the caller, the record and the column. A
  token from another caller, tampered or expired, is refused with `0x80040203` and the same message whatever the reason,
  so it does not reveal whether a session exists. Sessions (`meta.filesession`) survive restarts; they expire 24 hours
  after their last use.
- An hourly cleanup sweep removes expired sessions and their staged blocks, and blobs without a `fileattachment` row
  older than 24 hours.

## Commit, cascades and failures

- A commit replaces the column's previous file. The new blob is written before the transaction commits and deleted if it
  rolls back (for example when a plug-in on the `Update` throws); the previous blob is deleted after the commit.
- Deleting a record deletes its files; dropping a file or image column deletes all its files; deleting a table (also by a
  cascade solution delete) deletes all the files of that table. Blobs are deleted after the transaction commits, by an
  in-process background queue (the request does not wait for the store). A failed blob deletion is logged and retried by
  the sweep, never reported to the caller; so is one that never ran (the queue was full, or the process stopped first).

## Note bodies

The attached file of a note (`annotation.documentbody`, see [Activities and notes](activities.md#notes-and-attachments))
shares this store. Each body is a `fileattachment` row (`objectid` the note, `regardingfieldname` `documentbody`) and a blob
`{organization database}/{fileattachmentid}`, written in the note's transaction with the same commit and rollback rules as a
file column. `annotation` is always a target of `fileattachment.objectid` (relationship `annotation_FileAttachments`, our own
name), so the cleanup sweep never takes a body for an orphan; deleting a note, or the record it belongs to, deletes its body
after the commit; backups and restores carry the bodies with the other files. The file messages and routes do not serve
note bodies: write and read `documentbody` with Create, Update, Retrieve and RetrieveMultiple. A note body's `fileattachment`
row is readable only through Read on the note.

## Backup and restore

`org backup` writes `{db}-{timestamp}.files.tar` beside the `.bak`, and `org restore` needs it
unless `--no-files` is passed: see [Organizations and backups](cli.md#organizations-and-backups) for the volumes,
`Admin:FilesBackupDirectory`, the `missingFiles` report and exit code 7.

## Known differences from Dataverse

None of these is silent: each is refused with an explicit error or stated here.

- `DownloadBlock` returns at most 4 MB per block: a larger `BlockLength` is refused with an explicit `0x80040203`
  (Dataverse publishes no limit). Read a large file in several blocks.
- `DownloadBlock` with an `Offset` equal to the file size (past the last byte) is refused (`0x80040203`), except at offset
  0 of an empty file.
- Read and Write are checked when a session opens, not again for each block: the token is bound to the caller, the record
  and the column, and stays valid for 24 hours after its last use.
- Images are decoded up to 64 Mi pixels (67,108,864): a larger one is refused with `0x80072553` (a server-side bound, not
  a Dataverse rule). JPEG decodes at a reduced scale; a PNG, GIF or BMP near the bound can take about 256 MB of memory
  while its thumbnail is made, with at most one decode per processor at a time. EXIF orientation is not applied to
  thumbnails.
- Web API: the property routes `{entityset}({id})/{property}` serve file and image columns only. A single-property `GET`
  or `DELETE` of any other column (Dataverse supports, for example, `GET accounts(<id>)/name`) is not supported and is
  refused with 400 `0x80040203`; an unknown property is a 400 too (Dataverse probably answers 404 `0x80060888`). A
  malformed `Range` (several ranges, a suffix range, another unit) is a 400.
- A file name that is not printable ASCII, or that contains `%`, is percent-encoded (UTF-8) in the `x-ms-file-name`
  response header, so decoding the header always gives the name back (`100%25.txt` is sent as `100%2525.txt`); any other
  ASCII name is sent as is. How Dataverse encodes such names is not documented.
- An upload session stages at most 50,000 blocks before its commit: a new block beyond that is refused with `0x80040203`
  (a server-side bound, as an Azure block blob's block list; Dataverse publishes none).
- `DeleteFile` with the image id of an image kept without its full-size image (`CanStoreFullImage` false when it was set)
  answers 404 `0x80040217`: such an image has no `fileattachment` row. Clear it with an `Update` of the column to null.
  What Dataverse answers there is not documented.
- Asynchronous plug-in steps on `UploadBlock`, `DownloadBlock` or the `Update` an image commit makes keep their execution
  context, block or image bytes included, in the `asyncoperation` row until they run: an exception to "no blobs in SQL"
  (thumbnails being the other one). Register such steps synchronously, or on other messages, to keep file content out of
  the database.
- If the commit of a chunked Web API upload fails after its last chunk was received, sending that chunk again is refused
  (the chunk is already staged): start a new upload session.
- The Web API's file routes are executed as the file messages, so their plug-in steps fire: a `PATCH` upload fires steps on
  `InitializeFileBlocksUpload`, `UploadBlock` and `CommitFileBlocksUpload`; a `GET …/$value` of a file or full image (and
  `/Image/download.aspx?…&Full=true`) fires steps on `InitializeFileBlocksDownload` and `DownloadBlock`; a thumbnail
  download fires steps on `Retrieve`. Whether Dataverse does the same is not documented.
- `$value?size=full` (and `/Image/download.aspx?…&Full=true`) on an image kept without its full-size image answers 204 with
  no body. The download messages answer 404 `0x80040217` in the same case.
- A `$select` of an empty column leaves the property out of the Web API response, where Dataverse returns `"<name>": null`.
  This is general xrmverse Web API behaviour (every column type: null values are omitted when rows are read), not specific
  to file and image columns; it is on the backlog below.
- The `customizations.xml` shape of file and image attributes (`Type` `file` / `image`, `MaxSizeInKB`,
  `CanStoreFullImage`, `IsPrimaryImage`, `MaxHeight`/`MaxWidth` 144) follows the SDK property names and has not been
  checked against a real Dataverse export. Companion columns and `{table}_FileAttachments` are never exported; an import
  that finds them tolerates them.
- `fileattachment.objectid` is a polymorphic lookup to every table with a file or image column, but `$metadata` exposes its
  navigation property for the first target only. `$expand=objectid_{table}` works for every target (for example
  `fileattachments(<id>)?$expand=objectid_annotation` returns the note with its body), but only the first target's
  navigation is declared; in a new organization that is `annotation`.
- Backlog (not yet):
  - the hourly cleanup sweep lists the whole `{database}/` prefix of the store on each run, which grows with the number
    of files: a scale limit for very large stores (an incremental or event-driven sweep would remove it);
  - Web API responses that return `null` for the empty properties of a `$select` (see above).

## See also

- [Configuration](configuration.md)
- [Activities and notes](activities.md#notes-and-attachments)
- [Field-level security](field-security.md)

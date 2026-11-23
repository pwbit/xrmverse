# Email and queues

[← xrmverse documentation](../README.md#documentation)

## Introduction

A **queue** is a shared list of work. A record that someone must handle (an email, a task, a phone call, an appointment or a
row of a custom activity table) is put in a queue as a **queue item**; a worker **picks** the item, works on it, and either
**releases** it back or removes it. Queues are what a contact center or a shared mailbox is built on: customers' messages
arrive in a team queue, and agents take them one by one. xrmverse follows the Dataverse model of tables, messages and error
codes for queues. Whether each detail matches a real Dataverse is not verified (see
[Known limits](#known-limits)).

**Outgoing email** (roadmap item 4.1b) sends an email for real: `SendEmail` with `IssueSend` true hands the email to the
**mailbox** of its sender (a user or a queue), and a background job sends it through the **email server profile** of that
mailbox, over SMTP or Microsoft Graph, with its attachments. A System Administrator sets up the profile, approves the
mailbox's address and tests the mailbox first (see [Outgoing email](#outgoing-email)).

This page covers queues (item 4.1a) and outgoing email (item 4.1b). Receiving email (item 4.1c) is not part of it yet.

![The queue item grid with the Pick, Release, Route and Remove from queue commands](images/queue-item-grid.png)

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Tables | `queue`, `queueitem`, `queuemembership` (seeded); type codes 2020, 2029 and 2027 | Same model; type codes from memory |
| Queue-enabled tables | `email`, `task`, `phonecall`, `appointment` and every custom activity | Same idea; `IsValidForQueue` can be switched on later, never off |
| Messages | `AddToQueue`, `RemoveFromQueue`, `RouteTo`, `PickFromQueue`, `ReleaseToQueue`, `AddPrincipalToQueue`, `RetrieveUserQueues` | Same names and parameters; behaviour in the details is partly from memory |
| Default queues | Every user and every owner team gets a private queue, named `<full name>` or `<team name>` | Same idea; whether access teams get one is not verified |
| Privacy | A private queue is visible to its members, its owner and the members of an owner team; only System Administrator bypasses it | Same idea |
| Security of the messages | Working an item needs the queue item privilege, its queue readable and Read on its record; `prvPickFromQueue` is not modelled (see [Security](#security-of-the-messages)) | **Differs** |
| Concurrency | No ETag or `If-Match` on queue items; the messages are serialized per record | **Narrower** |
| Counters | `numberofitems` and `numberofmembers` on a queue are not maintained | **Narrower** |
| Email tables | `emailserverprofile`, `mailbox`, `activitymimeattachment` (seeded); type codes 9605, 9606 and 1001 | Same model; type codes from memory |
| Email server profiles | SMTP (server type IMAP/SMTP, Basic authentication) and Exchange Online through Microsoft Graph (OAuth with Microsoft Entra ID, app-only token); any other server type, protocol or credential source is a 400 | **Narrower** |
| Mailboxes | One per user and per queue, created with it; approval of the address by a System Administrator and a successful test before it sends | Same idea |
| Sending | `SendEmail` with `IssueSend` true sends in the background, from the caller or from a queue the caller can read; retries 3 times on a temporary error | Same idea; a mailbox that cannot send fails the email at once (see [Known limits](#known-limits)) |
| Sender policy | `xrmverse_EmailSendFrom`: `userOrQueue` (default) or `queueOnly` | **Extra** (xrmverse extension) |
| Attachments | `activitymimeattachment` with its body in the file store, size limit and blocked extensions of the organization | Same idea; no chunked transfer |
| Tracking tokens | Off by default; on when the organization has a `trackingprefix` | **Differs**: a new Dataverse environment has them on (from memory) |
| Receiving email | Not available | **Narrower** (planned in 4.1c) |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Create a queue | `xrmd queue create`, or create a `queue` row | [Queues](#queues) |
| Put a record in a queue | `AddToQueue`, or **Add to queue** on a list | [Queue items](#queue-items) |
| Take an item to work on it | `PickFromQueue`, or **Pick** | [Working an item](#working-an-item) |
| Hand an item back, or to someone else | `ReleaseToQueue`, `RouteTo` | [Working an item](#working-an-item) |
| Add a person or a team to a queue | `AddPrincipalToQueue`, `xrmd queue member add` | [Members and privacy](#members-and-privacy) |
| Find the queues of a user | `RetrieveUserQueues` | [Members and privacy](#members-and-privacy) |
| Build a contact center on queues | Custom columns, steps, an application user | [Contact center use](#contact-center-use) |
| Set up an SMTP or Graph server | `xrmd mailbox profile create`, or the web admin's **Email** page | [Email server profiles](#email-server-profiles) |
| Get a mailbox ready to send | `xrmd mailbox approve`, `link`, `test`, or the Email page | [Mailboxes](#mailboxes) |
| Send an email | `SendEmail` with `IssueSend` true, or **Send** on the email form | [Sending](#sending) |
| Allow sending from queues only | `xrmverse_EmailSendFrom` = `queueOnly` | [Sender policy](#sender-policy-xrmverse_emailsendfrom) |

## Queues

A queue is owned by a user or a team and has a name (up to 200 characters), a visibility (`queueviewtype`: 0 Public, 1
Private) and a state (`statecode`: 0 Active, 1 Inactive). `queuetypecode` is 1 (public and private queues are the same table
with a different visibility). A public queue can be read by everyone who holds Read on `queue`; a private queue only by the
people described in [Members and privacy](#members-and-privacy).

**Default queues.** When a user or an owner team is created, a private queue is created for it, named after the user's
`fullname` or the team's name in angle brackets (`<Jane Example>`), and the principal's `queueid` points at it. It is the
queue `RouteTo` uses when the target is a user or a team. A queue that still holds active items cannot be deleted (400), and neither can a team whose
default queue does: move the items on first.

## Queue items

A `queueitem` links one record (`objectid`, with the table's logical name in `objectidtypecode`, which the Web API can select
and filter on) to one queue (`queueid`). It also holds the
`title`, the `workerid` (who works on it), `workeridmodifiedon`, `enteredon` and a state: `statecode` 0 Active (status 1) or 1
Inactive (status 2). The relationship from a queue-enabled table to its items is `{SchemaName}_QueueItem` (singular) for the seeded activity
tables, for example `Email_QueueItem`, and `{logicalname}_QueueItems` for any other table, custom activities included. The
Web API navigation is `objectid_{table}`. Deleting the record deletes its items.

- **One active item per record.** A record has at most one active queue item. The exception is an email with status Received,
  which may sit in several queues. A direct Create of a second active item is refused (400), like `AddToQueue` on a record
  that is in a queue already, except that `AddToQueue` onto the record's current queue updates the item there.
- **Only queue-enabled tables.** `AddToQueue` on a record of another table is a 400 that names the table. An inactive record
  is refused, except a Received email.
- **State follows the record.** When a record leaves the active state (an activity is completed or cancelled), its active items
  become inactive. Reactivating the record does not reactivate them. A Received email keeps its items.
- **Seeded views.** The tables come with the system views **Active Queue Items**, **All Queue Items** and **Active Queues**.

`xrmd queue items <queue>` lists the items of a queue, and a queue item grid in a model-driven app shows them (see
[Surfaces](#surfaces)).

## Working an item

All the messages run through the same engine as the SDK and the Web API, so plug-in steps, workflow triggers, auditing and
security apply to them. The Web API bindings are:

| Message | Web API | What it does |
|---|---|---|
| `AddToQueue` | `POST queues({id})/Microsoft.Dynamics.CRM.AddToQueue` with `Target`, optional `SourceQueue` and `QueueItemProperties` | Creates the item (or moves it from `SourceQueue`); answers `QueueItemId` |
| `RemoveFromQueue` | `POST queueitems({id})/Microsoft.Dynamics.CRM.RemoveFromQueue` | Deletes the item |
| `RouteTo` | `POST RouteTo` with `Target` (queue, user or team) and `QueueItem` | Moves the item to the queue, or to the default queue of the user or team; the worker is kept |
| `PickFromQueue` | `POST queueitems({id})/Microsoft.Dynamics.CRM.PickFromQueue` with `SystemUser` and `RemoveQueueItem` | Sets `workerid` and `workeridmodifiedon`; with `RemoveQueueItem` true it deletes the item instead |
| `ReleaseToQueue` | `POST queueitems({id})/Microsoft.Dynamics.CRM.ReleaseToQueue` | Clears `workerid`; the item stays in its queue |
| `AddPrincipalToQueue` | `POST queues({id})/Microsoft.Dynamics.CRM.AddPrincipalToQueue` with `Principal` | Makes a user (or each member of a team) a member of the queue |
| `RetrieveUserQueues` | `GET systemusers({id})/Microsoft.Dynamics.CRM.RetrieveUserQueues(IncludePublic=…)` | The queues of a user |

The SDK requests have the same names (`AddToQueueRequest`, `PickFromQueueRequest`, and so on). A missing, mistyped or unknown
parameter is a 400.

- **Pick** on an item that someone else works on replaces the worker, with no error. Picking an inactive item is a 400. A
  worker that does not exist is a 404.
- **Release** clears the worker and leaves `workeridmodifiedon` as it was.
- **Route** needs the target queue to be readable by the caller; to a user or team, that principal's default queue, which is
  private. In practice only a System Administrator, the queue's owner, or a member can route to another person's default
  queue.
- **Security.** See the table below. The privilege `prvPickFromQueue` is not modelled.

### Security of the messages

Two rules cover every change to a queue item. A queue is **readable** by the caller when it is public, or when the caller (or
one of its teams) owns it, is a member, or has it shared for Read (see [Members and privacy](#members-and-privacy)); the
caller also needs Read on `queue`. This is the same rule every query uses.

- **Working an existing item** (Pick, Release, Remove, the queue an item leaves when it is routed or moved by `AddToQueue`, and
  a direct Update or Delete of a `queueitem`) needs the queue item privilege for the operation (Write, or Delete for Remove and
  Delete), the item's current queue readable, and Read on the item's record.
- **Placing an item** (`AddToQueue`, the destination of `RouteTo`, and a direct Create of a `queueitem`) needs Create on queue
  item, the destination queue readable, and Read on the record.

An Associate or Disassociate that repoints a queue item's `queueid` or `objectid` (for example over `Email_QueueItem` or
`queue_entries`) is a change to the item and follows the same rules: the item is worked (Write on queue item, its queue
readable, Read on its record), and the new queue must be readable or the new record readable.

Write on the `queue` record itself is needed only to change the queue (its name, owner, visibility and so on), never to work its
items.

| Message | What the caller needs |
|---|---|
| `AddToQueue` | Read on the record, Create on queue item, and the destination queue readable. An item that is elsewhere is moved by an Update, which also needs Write on queue item and the queue it leaves readable |
| `RemoveFromQueue` | Delete on queue item, the item's queue readable, Read on its record; the delete of the item is then checked as a Delete |
| `RouteTo` | Write on queue item, the queue the item leaves readable, Read on its record; Create on queue item and the destination queue (the target queue, or the default queue of the target user or team) readable |
| `AddPrincipalToQueue` | Append To on the queue |
| `PickFromQueue` | Write on queue item, the item's queue readable, Read on its record; the worker may be any user |
| `ReleaseToQueue` | Write on queue item, the item's queue readable, Read on its record |
| `RetrieveUserQueues` | Nothing extra for the caller's own id; Read on queue at Global depth for another user |

The Basic User role holds Read on queue, and Create, Read, Write, Delete, Append and Append To on queue item at Global depth (the
table has no owner). So a user with only Basic User works the items of the public queues and of the private queues they are a
member of, for the records they can read; nothing more is needed. A System Administrator passes the privacy part of these
checks.

## Members and privacy

A user becomes a member of a queue through `AddPrincipalToQueue`, or by associating on `queuemembership_association` (the
relationship name is from memory). Adding a team adds each of its members; adding someone twice changes nothing. `xrmd queue
member add <queue> <user|team>` does this from the command line.

A **private** queue, and the items in it, are visible to:

- its members, added directly;
- its owner, and the members of its owner team;
- a user or team it is shared with for Read;
- a System Administrator (the only role that bypasses the rule).

Everyone else sees neither the queue nor its items, in every query form (RetrieveMultiple, FetchXML, `$expand`). A queue item is readable when both its queue and its record are.

`RetrieveUserQueues` answers a user's queues: the queues the user is a member of, plus the default queues of the user's teams,
ordered by name and including inactive queues. Without `IncludePublic` only private queues come back; with it, every public
queue the caller can read is included. Asking for another user needs the Read privilege at Global depth on queue (403 otherwise).

## Outgoing email

Three tables carry outgoing email, all seeded in every organization:

- **`emailserverprofile`**: how to reach a mail server (SMTP, or Exchange Online through Microsoft Graph) and, when the
  profile holds them, the credentials. Only a System Administrator works with profiles (Basic User has no right on the table).
- **`mailbox`**: one per user and one per queue, created with the user or the queue (and at startup for users and queues
  that existed before). It points at a profile and keeps the result of the last test and the last error. The user or queue
  points at it through `defaultmailbox`.
- **`activitymimeattachment`**: the attachments of an email (see [Attachments](#attachments)).

An email leaves only when its sender's mailbox **can send**: the sender's address is approved, the mailbox is active, is
linked to a profile, uses Server-Side Synchronization for outgoing email (`outgoingemaildeliverymethod` 2) with
`enabledforoutgoingemail` on, and its last test succeeded (`outgoingemailstatus` 1 Success).

### Email server profiles

xrmverse accepts two kinds of profile. Every other combination of server type, authentication protocol and credential
source is refused with a 400 that names the value.

| Kind | `servertype` | `outgoingauthenticationprotocol` | `outgoingcredentialretrieval` | Other columns |
|---|---|---|---|---|
| SMTP | 4 IMAP/SMTP | 3 Basic (0 Auto Detect is stored as 3) | 1 the profile's credentials, or 0 each mailbox's own | `outgoingserverlocation`, `outgoingportnumber`, `outgoingusessl`, `outgoingusername`, `outgoingpassword` |
| Graph | 0 Exchange Server | 4 OAuth | 7 OAuth with Microsoft Entra ID | `exchangeonlinetenantid`, `oauthclientid`, `oauthclientsecret`, `serverauthority` 0 Public or 3 Automatic |

**SMTP.** `outgoingusessl` true opens TLS from the first byte (usually port 465); false requires STARTTLS (usually port
587). A server that offers no STARTTLS is refused before any credential is sent, unless the server setting
`Mail:AllowInsecureSmtp` is true (see [Configuration](#configuration)). Certificates are always validated. With credential
retrieval 1 one user name and password on the profile serve every mailbox of the profile; with 0, each mailbox has its own
`username` and `password`, which its owner may set.

**Graph.** The profile sends as any mailbox of the tenant with an app-only token (client credentials). Set it up once per
profile:

1. Register an application in Microsoft Entra ID for the tenant and create a client secret for it.
2. Grant it the **application** permissions `Mail.Send` and `Mail.ReadWrite` of Microsoft Graph, with admin consent. The
   message is created as a draft from the MIME message and then sent, so that the `Message-ID` is the one xrmverse wrote;
   the draft needs `Mail.ReadWrite`.
3. Limit the application to the mailboxes it should serve with an Exchange Online **application access policy** (a
   mail-enabled security group holding those mailboxes). Without one, the application can send as every mailbox of the
   tenant.
4. Create the profile with the tenant id (or domain), the application's client id and the secret.

**Secrets.** `outgoingpassword`, `incomingpassword`, `oauthclientsecret` and `mailbox.password` are write-only: no read
returns them (Retrieve, RetrieveMultiple, FetchXML, `$select`, audit, plug-in images and contexts). Their `is…set` columns
(`isoutgoingpasswordset`, `isincomingpasswordset`, `isoauthclientsecretset`, `mailbox.ispasswordset`) say whether one is
stored. They are stored unencrypted in the organization database,
as the webhook authentication value is (see [Known limits](#known-limits)).

```bash
# SMTP, credentials on the profile; the password is read from standard input
printf '%s\n' "$SMTP_PASSWORD" | xrmd mailbox profile create --name "Relay" --type smtp \
  --server smtp.example.com --port 587 --ssl false --username relay@example.com --secret-stdin
# Graph; the client secret is asked for on the terminal
xrmd mailbox profile create --name "Exchange Online" --type graph --tenant example.com --client-id <application id>
xrmd mailbox profile list
```

A secret is never a command-line argument: `--secret-stdin` reads one line from standard input, otherwise the CLI asks for
it on the terminal without echo. The web admin's **Email** page creates and edits profiles too (see
[Web admin](web-admin.md#email)).

### Mailboxes

Every user and every queue gets a mailbox, named after it, with its email address (`internalemailaddress` of a user,
`emailaddress` of a queue). Give a new queue its address with `xrmd queue create <name> --email <address>`; for an existing
queue or user, set the address on its record (in a model-driven app, in the web admin's **Tables**, or with the Web API). A new
mailbox gets the organization's default profile (`organization.defaultemailserverprofileid`) when one is set, and no profile
otherwise. Its incoming and outgoing delivery methods are those of its user or queue (None when they are empty), and they follow
it: writing `incomingemaildeliverymethod` or `outgoingemaildeliverymethod` on the user or queue writes the same value on its
mailbox, as in Dataverse, when the value differs from the one stored on the user or queue (writing back an unchanged
value leaves a mailbox linked directly alone). A profile alone does not make the mailbox send. To make it send:

1. **Approve the address.** A System Administrator sets `emailrouteraccessapproval` to 1 Approved on the user or the queue
   (`xrmd mailbox approve <mailbox>`, or **Approve** on the Email page). A new address starts as 2 Pending Approval. Changing
   the address of a user or queue resets the approval to Pending Approval (Empty when the address is removed) and the
   mailbox's test results to Not Run. Only a System Administrator may write the approval (403 otherwise).
2. **Link the mailbox to a profile.** A System Administrator sets `emailserverprofile`, `outgoingemaildeliverymethod` 2
   (Server-Side Synchronization) and `enabledforoutgoingemail` true on the mailbox: `xrmd mailbox link <mailbox> --profile
   <profile>`, or **Link to profile** on the Email page. Both send the same write as this Web API request:

   ```http
   PATCH /api/data/v9.2/mailboxes(<mailboxid>)
   {
     "emailserverprofile@odata.bind": "/emailserverprofiles(<profileid>)",
     "outgoingemaildeliverymethod": 2,
     "enabledforoutgoingemail": true
   }
   ```

   Delivery methods 1 (Outlook) and, for incoming email, 3 (Forward Mailbox) are refused on `mailbox`, `systemuser` and
   `queue`.
3. **Test it.** Writing `testemailconfigurationscheduled` true queues a test job (`xrmd mailbox test <mailbox> --wait`, or
   **Test** on the Email page). Only the mailbox's owner (the owning user, or a member of the owning team) or a System
   Administrator may start or withdraw a test. The test checks the approval, the mailbox, the profile and the credentials,
   then connects and signs in to the server without sending anything (Graph: it reads the mailbox's drafts folder). The
   outcome goes on the mailbox: `outgoingemailstatus` 1 Success or 2 Failure, `testmailboxaccesscompletedon`, and on failure
   `lastsyncerror` (the reason), `lastsyncerrorcount` and `lastsyncerroroccurredon`. The flag goes back to false.

```bash
xrmd mailbox list
xrmd mailbox approve support@example.com
xrmd mailbox test support@example.com --wait      # exit 5 when the test failed, with the reason
xrmd mailbox show support@example.com
```

### Sending

`SendEmailRequest { EmailId, IssueSend = true }` (Web API: `POST emails(<id>)/Microsoft.Dynamics.CRM.SendEmail` with
`{ "IssueSend": true }`) checks the email and its sender, sets it to Pending Send (state 1, status 6) and queues one send job
in the same transaction. **Send** on an email form does the same (see
[Model-driven apps](model-driven-apps.md#email-form)).

- **Sender.** The `from` party is the caller's own user (an empty `from` is filled with it) or a queue the caller can read
  (403 otherwise). Any other sender is a 400. The [sender policy](#sender-policy-xrmverse_emailsendfrom) may allow queues
  only.
- **Recipients.** At least one party in `to`, `cc` or `bcc`. A recipient that is only an address (no record) is a 400
  unless the organization's `allowunresolvedpartiesonemailsend` is on.
- **The job** runs as the caller. It builds the MIME message with the email's subject, its `description` as the HTML body,
  its parties and its attachments, stores the `Message-ID` in `email.messageid` and counts the attempt in
  `deliveryattempts` before it hands the message to the provider. Then:
  - **Sent:** state 1, status 3 Sent, `senton` now; the mailbox's `outgoingemailstatus` becomes Success.
  - **Temporary error** (the server is unreachable, an SMTP `4xx` answer, a Graph `429` or `5xx`): the job waits and tries
    again after 1, 5 and 15 minutes; each error goes on the mailbox (`lastsyncerror`, `lastsyncerrorcount`). After the third
    retry the email is Failed.
  - **Permanent error**, or a mailbox that cannot send (address not approved, no profile, not tested): the email is Failed
    (state 0, status 8) and the reason is the send job's message (`asyncoperation.message`). A failure of the server or of
    the credentials also sets the mailbox to Failure with the reason; a refused recipient fails only that email.
- A Failed email is open again: correct it and send it again.
- `IssueSend` false still marks the email as Sent without sending anything (see
  [Activities and notes](activities.md#sendemail)).

Each send job is a system job (`asyncoperation.operationtype` 99011, regarding the email; a mailbox test is 99012).

### Sender policy (`xrmverse_EmailSendFrom`)

The environment setting `xrmverse_EmailSendFrom` is an **xrmverse extension**: Dataverse has no such switch. It decides who
may be the sender of an email that leaves:

| Value | Effect |
|---|---|
| `userOrQueue` (default) | As in Dataverse: the caller's own user or a queue the caller can read |
| `queueOnly` | Only a queue the caller can read. `SendEmail` with `IssueSend` true is a 400 naming `xrmverse_EmailSendFrom` when `from` is a user or empty; the send job checks again, so an email queued before the switch fails with the same reason |

`IssueSend` false is not affected (nothing leaves). Set the value with the **Send only from queues** switch on the web
admin's Email page, or write an `organizationsetting` row for the setting definition `xrmverse_EmailSendFrom` (the same way
as `xrmverse_AppOnLoad`, see [Model-driven apps](model-driven-apps.md#xrmverse-extensions)). A solution that relies on the
setting does not carry it into Dataverse.

### Attachments

An attachment is an `activitymimeattachment` row whose `objectid` is the email, with `filename`, `mimetype` and `body`
(base64). The body lives in the file store, as a note's body does, and is read back with Retrieve or `$select=body`.

- Attachments are added and changed only while the email is open (Draft or Failed); they need Write on the email, and
  reading one needs Read on the email.
- A body larger than `organization.maxuploadfilesize`, or a file name whose extension is in
  `organization.blockedattachments`, is refused (400).
- `organization.blockedattachments` holds extensions separated by `;`. A new organization gets Dataverse's default list, as
  Learn gives it: `ade;adp;app;asa;ashx;asmx;asp;bas;bat;cdx;cer;chm;class;cmd;com;config;cpl;crt;csh;dll;exe;fxp;hlp;hta;htr;`
  `htw;ida;idc;idq;inf;ins;isp;its;jar;js;jse;ksh;lnk;mad;maf;mag;mam;maq;mar;mas;mat;mau;mav;maw;mda;mdb;mde;mdt;mdw;mdz;msc;`
  `msh;msh1;msh1xml;msh2;msh2xml;mshxml;msi;msp;mst;ops;pcd;pif;prf;prg;printer;pst;reg;rem;scf;scr;sct;shb;shs;shtm;shtml;`
  `soap;stm;tmp;url;vb;vbe;vbs;vsmacros;vss;vst;vsw;ws;wsc;wsf;wsh;svg`. An organization made before this version gets the same
  list once, at its first start, only when the column is empty: a list an administrator set is kept, and a list cleared
  afterwards is not set again. Change it by updating the `organization` row (for example with the Web API,
  `PATCH organizations(<id>)` with `blockedattachments`).
- `email.attachmentcount` follows the rows. Deleting the email deletes its attachments.
- `InitializeAttachmentBlocksUpload` and `InitializeAttachmentBlocksDownload` (chunked transfer) are refused: a body travels
  in one request.

See [Activities and notes](activities.md#what-is-refused) for the related refusals.

### Tracking tokens

Tracking tokens are **off** by default: the organization has no `trackingprefix`. A new Dataverse environment has them on
(from memory, checklist 7).
To turn them on, set `trackingprefix` (for example `CRM:`), `trackingtokeniddigits`, `nexttrackingnumber` and, optionally,
`maximumtrackingnumber` on the `organization` row. Then:

- `SendEmail` with `IssueSend` true uses the `TrackingToken` it is given, or else takes the next one: the prefix followed by
  the number padded to `trackingtokeniddigits` digits. It appends the token to the subject (unless the subject holds it
  already) and stores it in `email.trackingtoken`. A subject that already carries a token in the current format (the prefix
  followed by at least `trackingtokeniddigits` digits), such as a Failed email sent again, keeps that token: no new number is
  taken and nothing is appended. A subject that would become too long, or a number past `maximumtrackingnumber`, is a 400.
- `trackingtokenidbase` is not supported: writing a value other than empty or 0 is a 400 that names the column.
- `GetTrackingTokenEmail` (SDK, or `POST /api/data/v9.2/GetTrackingTokenEmail`) returns the next token; with tracking off it is
  a 400 that names `trackingprefix`.

Incoming email (item 4.1c) will use the token to link replies.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `Mail:AllowInsecureSmtp` | `false` | Allow an SMTP session without TLS when the server offers no STARTTLS. For test and development servers only: user names and passwords then travel in clear text |
| `Mail:GraphBaseUrl` | `https://graph.microsoft.com/v1.0` | The Microsoft Graph endpoint the Graph profiles call |

They are ASP.NET configuration keys: as environment variables, write them as `Mail__AllowInsecureSmtp`. See
[Configuration](configuration.md).

## Tasks

### Put an email in a queue and work it

```bash
xrmd queue create "Support"
xrmd queue member add "Support" jane@example.com
xrmd queue items "Support"
```

The queue commands above set up the queue. To put an email in it with the SDK, create the email, then run `AddToQueueRequest { Target = new EntityReference("email", id),
DestinationQueueId = queueId }`. The worker runs `PickFromQueueRequest { QueueItemId = item, WorkerId = workerId }`, and later
`ReleaseToQueueRequest`, `RouteToRequest` (to a private escalation queue, for example) or `RemoveFromQueueRequest`.

### Send an email from a queue

```bash
printf '%s\n' "$SMTP_PASSWORD" | xrmd mailbox profile create --name "Relay" --type smtp \
  --server smtp.example.com --port 587 --ssl false --username relay@example.com --secret-stdin
xrmd queue create "Support" --email support@example.com
xrmd mailbox link support@example.com --profile "Relay"
xrmd mailbox approve support@example.com
xrmd mailbox test support@example.com --wait
```

With the SDK, create the email with `from` = the queue and `to` = a contact, add an `activitymimeattachment`, then run
`SendEmailRequest { EmailId = id, IssueSend = true }`. The email is Pending Send until the job ends with Sent or Failed.

### Use the command line

| Command | Does |
|---|---|
| `xrmd queue list [--public]` | Lists queues with their visibility and state; `--public` shows only public ones |
| `xrmd queue create <name> [--private] [--email <address>]` | Creates a queue, public unless `--private`, with its email address when given |
| `xrmd queue member add <queue> <user\|team>` | Adds a user or a team; the queue is named by name or id |
| `xrmd queue items <queue>` | Lists the items of a queue |

An unknown queue, user or team exits 4 (not found); a name that matches several queues exits 5 (use the id). Other refusals
are the server's (see [CLI](cli.md#exit-codes)).

| Command | Does |
|---|---|
| `xrmd mailbox list` | Lists the mailboxes: address, approval, profile, outgoing delivery and the last test |
| `xrmd mailbox show <id\|address>` | One mailbox, its profile, its last error and whether its password is set |
| `xrmd mailbox approve <id\|address>` | Approves the address of the mailbox's user or queue (System Administrator) |
| `xrmd mailbox link <id\|address> --profile <id\|name>` | Links the mailbox to a profile, with Server-Side Synchronization and outgoing email on (System Administrator) |
| `xrmd mailbox test <id\|address> [--wait] [--timeout N]` | Starts a test; with `--wait` prints the result, exit 5 when it failed and 6 when it did not run in time |
| `xrmd mailbox profile create\|update\|list` | Email server profiles (`--type smtp\|graph`); the secret only with `--secret-stdin` or a prompt |

## Surfaces

- **Model-driven apps.** The list command bar has **Add to queue** on queue-enabled tables, and **Pick**, **Release**,
  **Route** and **Remove from queue** on the queue item grid (see [Model-driven apps](model-driven-apps.md)).
- **CLI.** `xrmd queue` (see [CLI](cli.md)).
- **Web API, SDK, SOAP.** The messages above.
- **Web admin.** Queues are tables like others; there is no queue-specific page. A table's page has a one-way switch to
  enable queues for the table (`IsValidForQueue`). The **Email** page (System Administrators) lists profiles and mailboxes,
  approves and tests mailboxes and sets the sender policy (see [Web admin](web-admin.md#email)).
- **Model-driven apps.** The email form: party lists, **Send**, the send status and attachments (see
  [Model-driven apps](model-driven-apps.md#email-form)).
- **CLI.** `xrmd mailbox` for profiles and mailboxes.

## Contact center use

A contact center needs more than the plain queue, and the following is allowed:

- **Your own columns, forms and views** on `queue` and `queueitem`, created like any custom column.
- **Plug-in steps and webhooks** on the queue messages (`AddToQueue`, `RemoveFromQueue`, `RouteTo`, `PickFromQueue`,
  `ReleaseToQueue`, `AddPrincipalToQueue`, `RetrieveUserQueues`) and on Create, Update and Delete of `queueitem`.
- **Picking for someone else.** An application user (a service client) can pick an item on behalf of a worker by passing that
  worker's id as `WorkerId`, so a routing service can assign work without signing in as each agent. It needs the rights of
  any worker: Write on queue item, Read on the records, and each private queue it serves readable (as a member, for example).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **Counters.** `queue.numberofitems` and `numberofmembers` are not maintained.
- **No optimistic concurrency.** Queue items have no ETag and no `If-Match` check; the messages are serialized per record, and
  the last writer wins on a plain Update.
- **Delete during add.** Deleting a queue while an item is being added to it at the same moment can remove the item that was
  just added.
- **Privilege model.** `prvPickFromQueue` is not modelled. Pick and Release need Write on queue item, the item's queue
  readable and Read on its record instead, like every change to an item (see [Security](#security-of-the-messages)).
- **Steps on automatic changes.** An item that the server deactivates itself, because its record was completed or cancelled,
  runs no plug-in steps; only the messages above do.
- **Migration.** Queues, queue items and memberships are not part of a migration from Dataverse yet (see
  [Migrating from Dataverse](migration.md)).
- **Not modelled.** Routing rules, and the Unified Routing and Omnichannel tables.
- **No incoming email.** Receiving email, and linking replies through tracking tokens, are roadmap item 4.1c.
- **Secrets stored unencrypted.** Profile and mailbox passwords and the client secret are kept unencrypted in the
  organization database (never returned by a read), as the webhook authentication value is (#86).
- **Tracking tokens off by default.** A new organization has no `trackingprefix`; a new Dataverse environment has tracking
  tokens on (from memory). Set the prefix to turn them on (see [Tracking tokens](#tracking-tokens)).
- **A mailbox that cannot send fails the email.** When the sender's mailbox is not approved, has no profile or has not passed
  its test, the send job sets the email to Failed (state 0, status 8) with the reason on the job. From memory, Dataverse leaves
  such an email Pending Send until the mailbox works (checklist 8).
- **Interrupted send.** A send job interrupted after it started an attempt (the server stopped, for example) is not run again,
  so that the email is never sent twice: the email stays Pending Send. Check whether it arrived, then send it again.
- **Graph timeouts.** When a Graph send times out after Graph accepted the message, the retry can send it a second time.
- **Reopen and send again during a send.** Reopening an email that is Pending Send and sending it again while its first send
  job is still handing it to the server can deliver it twice.
- **Stop during a send.** A graceful stop of the server while a send job prepares the message, before it is handed to the
  server, also leaves the email Pending Send although nothing was sent. Reopen the email, then send it again.
- **Queue items after a failed send.** Sending an email deactivates its active queue items; when the send fails and the email
  is open again (Failed), those items stay inactive.
- **Tracking token base.** `organization.trackingtokenidbase` is refused unless empty or 0: tokens are the prefix and the
  counter only.
- **Temporary recipient errors.** A temporary error about one recipient (for example SMTP `452`) that is still there after
  the last retry sets the mailbox to Failure, not only the email.
- **Columns the send job writes are not audited.** `messageid` and `deliveryattempts`, written by the send job, do not appear
  in the audit history of the email; the change of state does.
- **The send job's link to the email** is `asyncoperation.regardingobjectid`, a plain id column, not a lookup: filter on it
  by value (`regardingobjectid eq <email id>`), it has no navigation property.
- **Not modelled.** Sending as another user, delegate access to a mailbox, email templates (`SendEmailFromTemplate` and the
  template messages are refused), Outlook and Forward Mailbox delivery, IMAP/SMTP with OAuth, Exchange on-premises, and
  inline (content id) attachments.
- **Migration.** Mailboxes, profiles and email attachments are not part of a migration from Dataverse.
- **SMTP without TLS** works only with `Mail:AllowInsecureSmtp` true, a setting for test and development servers.

### Not yet verified against Dataverse

These behaviours were written from memory. Each is on the checklist of the plan, to be compared on a licensed Dataverse
environment:

- The type codes 2020, 2029 and 2027, and the relationship names `queue_team`, `{SchemaName}_QueueItem` (we use the singular; custom tables get `{logicalname}_QueueItems`) and
  `queuemembership_association`.
- The rights of Basic User on queue and queue item (Read on queue; Create, Read, Write, Delete, Append and Append To on queue
  item here), and that working an item needs no Write on its queue.
- The default queue's name, and whether access teams get one.
- `PickFromQueue` on an item someone else works on (the worker is replaced here), and on an inactive item (400 here).
- Reactivating an activity and its old queue item (the item stays inactive here).
- That a record leaving the active state deactivates its items with state 1 and status 2, except a Received email.
- Whether that automatic deactivation of a queue item fires `Update` steps on `queueitem` (none do here).
- `PickFromQueue` with `RemoveQueueItem` true (deletes without writing the worker), `workeridmodifiedon` set on Pick and left
  as it is on Release, and a missing worker being a 404 even then.
- `RetrieveUserQueues`: private queues only without `IncludePublic`, the meaning of "the user's queues", inactive queues
  included, order by name, the Global Read right needed for another user, and the plug-in context (`systemuser`, the user's
  id).
- The behaviour of `RetrieveUserQueues` when `UserId` is another user (403 here unless the caller has Global Read on queue).
- Whether `RouteTo` to a user or team needs read access to that principal's private default queue.
- `AddToQueue` refusing an inactive record (except a Received email); `RouteTo` keeping the worker; `AddToQueue` onto the
  current queue updating the existing item.
- Deleting a queue that holds active items being a 400.
- The type codes 9605 (`emailserverprofile`), 9606 (`mailbox`) and 1001 (`activitymimeattachment`).
- That an Exchange Online profile has server type 0, and the protocol and credential values it uses.
- The rights of Basic User on `mailbox` (Read and Write on their own) and on `emailserverprofile` (none).
- That changing a user's or queue's address resets the approval to Pending Approval.
- Adding or deleting an attachment of a sent email (adding is refused here, deleting is not).
- That Graph keeps the `Message-ID` of a message created from MIME.
- The tracking token's format, how "use tracking tokens" is stored, that `SendEmail` appends the token to the subject, and
  that `GetTrackingTokenEmail` needs no privilege beyond being signed in.
- The state, status and `deliveryattempts` of an email after a failed send, whether a Failed email keeps `senton`, and what
  Dataverse does with an email whose sender's mailbox cannot send.
- Whether a new Dataverse environment has tracking tokens on.
- Whether a Failed email sent again keeps its tracking token (kept here, when the subject carries it in the current format).
- The name of the type column of `queueitem.objectid` in the Web API (`objectidtypecode` here).
- That writing `testemailconfigurationscheduled` starts a mailbox test, who may do it, and whether a mailbox whose address is
  not approved can be tested (here the test fails with the reason, without contacting the server).

## See also

- [Activities and notes](activities.md)
- [Model-driven apps](model-driven-apps.md)
- [CLI](cli.md)
- [Security](security.md)
- [Plug-ins](plugins.md)
- [Known issues and limitations](known-issues.md)

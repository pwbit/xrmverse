# Activities and notes

[← xrmverse documentation](../README.md#documentation)

## Introduction

An **activity** is a record of something done or to do with someone: an email, a task, a phone call, an appointment, or a
row of a custom activity table. Every activity can be **regarding** one record (an account, a contact, a custom table), and
it lists its **parties** (sender, recipients, attendees) in party list columns. Every activity is also readable through one
shared table, `activitypointer`, so one query finds all the activities of a record whatever their type. A **note**
(`annotation`) is a text with an optional attached file, linked to one record. A **post** is a short message about one record, with
replies and @mentions (see [Posts](#posts)). xrmverse follows the Dataverse model of tables,
columns, messages and error codes. Whether each detail matches a real Dataverse is not verified. The Microsoft documentation describes the model in
[Activity tables](https://learn.microsoft.com/power-apps/developer/data-platform/activity-entities),
[Activities data model and storage](https://learn.microsoft.com/power-apps/developer/data-platform/activities-data-model-storage),
[ActivityParty table](https://learn.microsoft.com/power-apps/developer/data-platform/activityparty-entity),
[Custom activities](https://learn.microsoft.com/power-apps/developer/data-platform/custom-activities) and the table
references of [activitypointer](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/activitypointer),
[email](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/email) and
[annotation](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/annotation). This page says
what xrmverse does and where it stops.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Tables | `activitypointer`, `activityparty`, `email`, `task`, `phonecall`, `appointment`, `annotation`, custom activities | Same model; object type codes from memory |
| `activitypointer` | A stored copy written in the same transaction | **Differs** in storage; same query behaviour |
| Sending email | `SendEmail` with `IssueSend` true sends through the sender's mailbox (SMTP or Microsoft Graph); the sender is the caller or a queue the caller can read (see [Email and queues](email-and-queues.md#outgoing-email)) | Same idea; **Narrower** in the details |
| Queues | Queue items for the activity tables (see [Email and queues](email-and-queues.md)) | Same idea |
| Email attachments | `activitymimeattachment`, body in the file store, sent with the email | Same idea; no chunked transfer |
| Recurring appointments, templates | Refused | **Narrower** (see [What is refused](#what-is-refused)) |
| Note attachments | One piece, up to 125 MiB | **Narrower**: no chunked transfer |
| Posts | Create, read, delete, replies, @mentions; cannot be updated; auto posts refused | **Narrower** (see [Posts](#posts)) |
| Verification | Several behaviours not verified on a real Dataverse | **Unverified** where stated |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Make a table an activity, or give it notes and activities | `xrmd table create --activity`, `--notes`, `--activities` | [Custom activities](#custom-activities), [Regarding and notes](#regarding-and-notes-hasactivities-and-hasnotes) |
| Find every activity of a record | Query `activitypointer` on `regardingobjectid` | [The tables](#the-tables) |
| Add senders, recipients, attendees | Party list columns | [Party lists](#party-lists) |
| Complete or cancel an activity | `SetState` | [State and status](#state-and-status) |
| Attach a file to a record | A note (`annotation`) | [Notes and attachments](#notes-and-attachments) |

## The tables

Every organization is seeded with seven tables (the object type codes are from memory, checklist 1a):

| Table | Entity set | Type code | Owner | What it holds |
|---|---|---|---|---|
| `activitypointer` | `activitypointers` | 4200 | user | one read-only row per activity, with the common columns |
| `activityparty` | `activityparties` | 135 | none | one row per party of an activity |
| `email` | `emails` | 4202 | user | emails |
| `task` | `tasks` | 4212 | user | tasks |
| `phonecall` | `phonecalls` | 4210 | user | phone calls |
| `appointment` | `appointments` | 4201 | user | appointments |
| `annotation` | `annotations` | 5 | user | notes and their attached files |

### Common columns

Every activity table and `activitypointer` have `activityid` (the primary key, the same id in the
activity's table and in the pointer), `subject` (the primary name), `description`, `activitytypecode` (the activity's table,
read-only), `regardingobjectid` with `regardingobjecttypecode`, `scheduledstart`, `scheduledend`, `actualstart`,
`actualend`, `scheduleddurationminutes`, `actualdurationminutes`, `prioritycode` (0 Low, 1 Normal, 2 High; default 1),
`isbilled`, `isworkflowcreated`, the read-only `isregularactivity`, `transactioncurrencyid` and `exchangerate` (as for any
table with money, see [Currencies](currencies.md); the relationships are `TransactionCurrency_ActivityPointer`,
`TransactionCurrency_Email`, …), `statecode`, `statuscode` and the owner and system columns.

### Own columns

`email`: `directioncode` (default true), `trackingtoken`, `senton` (read-only), `messageid` and the party
lists `from`, `to`, `cc`, `bcc`, `related`. `task`: `percentcomplete` (0 to 100), `category`, `subcategory`, no party list.
`phonecall`: `phonenumber`, `directioncode`, `leftvoicemail`, party lists `from`, `to`, `related`. `appointment`:
`location`, `isalldayevent`, party lists `requiredattendees`, `optionalattendees`, `organizer`. Any other standard column
of these tables (`torecipients`, `sender`, `sortdate`, …) is not seeded: reading it is the usual "attribute not found" 400.

### EntityName columns

`activitytypecode`, `regardingobjecttypecode`, `objecttypecode` (on `annotation`) and
`partyobjecttypecode` are EntityName columns: their value is a table's logical name (`"email"`, `"account"`), in the SDK
and on the Web API. The three `…typecode` columns that go with a lookup are read from the lookup itself: a value you send is
accepted only when it names the lookup's table, otherwise 400. Filters on them take a logical name (`eq 'email'`) or the
type code. Only the seed creates EntityName columns: `CreateAttribute` with that type is a 400.

### `activitypointer` is a kept copy

Each activity table holds the full row of its activities. The server writes the
pointer row in the same transaction as every Create, Update, Assign, SetState and Delete of an activity, and queries on
`activitypointer` are plain queries on one table: FetchXML, QueryExpression, the Web API, paging and the security filter
work on it as on any table. The common columns are therefore stored twice. A `subject` longer than the pointer's 400
characters (an email's can be 800) is cut to 400 in the pointer, and a `description` longer than the pointer's 2000 is cut
to 2000; the email keeps the full text. An `activityid` is unique across all activity tables, as in Dataverse, where every
activity is a row of one base table: a Create with the id of an existing activity of another table (a `task` with an
email's id, say) is refused with `DuplicateRecord` (`0x80040237`, 412, "A record that has the attribute values
activityid = … already exists"), the same fault as a duplicate id in one table, and changes nothing. Create,
Update, Delete, Assign, SetState, GrantAccess and ModifyAccess of `activitypointer` are refused (400) for every caller,
System Administrators included; reads, `Associate` and `Disassociate` are its only messages.

**Two settings columns.** `systemuser.internalemailaddress` (the address a user party gets) and
`organization.maxuploadfilesize` (the largest note body in bytes, default 5242880, 0 to 131072000). An organization created
before these tables gets them, their relationships and the defaults at startup; a second start changes nothing.

## Regarding and notes: HasActivities and HasNotes

Two table settings decide which tables activities and notes can point to:

- **`HasActivities`**: the table's records can be the regarding record of activities. Its table becomes a target of
  `regardingobjectid` on `activitypointer` and on every activity table.
- **`HasNotes`**: the table's records can have notes. Its table becomes a target of `annotation.objectid`.

`account` and `contact` have both. The four standard activity tables have `HasNotes`. Every other seeded table has
neither. You set them with `CreateEntity` (the `EntityMetadata` properties, or `CreateEntityRequest.HasActivities`/
`HasNotes`), `UpdateEntity`, `PUT EntityDefinitions(...)`, a solution import (`HasRelatedActivities`/`HasRelatedNotes`),
`xrmd table create --notes --activities`, `xrmd table set`, a schema file (`hasNotes`, `hasActivities`) and the web admin.

- **They are one-way.** False to true works; true to false is a 400. A solution import of `0` on a table that is on keeps
  it on and logs a warning.
- `HasActivities` is refused (400) on activity tables, `activitypointer`, `activityparty`, `annotation` and intersect tables.
  `HasNotes` is refused on `activityparty`, `annotation` and intersect tables. Organization-owned tables may have both.
- A false `CreateEntityRequest.HasActivities`/`HasNotes` parameter is ignored: the SDK always sends both, so only true asks
  for something.

Each pair of target table and activity table gets a saved relationship with the Dataverse name: `Account_Emails`,
`Account_Tasks`, `Account_Phonecalls`, `Account_Appointments` and `Account_ActivityPointers` for `account` (and `Contact_…`
for `contact`), `{table}_{collection}` for any other table, where `{collection}` is the activity's collection schema name
(`Emails`, …; for a custom activity its `CollectionSchemaName`, else its SchemaName plus `s`). The navigation property is
`regardingobjectid_{table}` on `activitypointer` and `regardingobjectid_{table}_{activity}` on an activity table. For notes:
`Account_Annotation`, `Contact_Annotation`, `Email_Annotation` and so on for the seeded tables, `{table}_Annotations` for a
custom table, navigation `objectid_{table}`. These names, apart from the account ones, are from memory (checklist 2a). You
cannot create, update or delete these relationships, nor change the targets of the lookups yourself (400): the server
keeps them in step with the settings.

**Deleting.** Deleting a record deletes its activities and notes (Delete Cascade), and with them the activities' pointer
and party rows and the notes' files. A regarding, note or other multi-table lookup names the deleted record by id **and**
table, so the Delete of contact X neither deletes the activities and notes of an account that has the same id nor clears a
Customer lookup that names that account (RemoveLink); Restrict counts only that table's records too. Deleting a **table** is refused (400) while any activity is regarding one of its
records or any note is attached to one; delete or move them first. The same check runs in a managed solution uninstall,
dry run included, so the uninstall is refused before anything changes. The uninstall checks the caller's privileges first:
a caller who may not uninstall the solution gets 403 and never this refusal, which names the counts of activities and notes.

## Custom activities

A custom activity is a table created with `IsActivity` true. It gets the common columns, the party lists `from`, `to`,
`cc`, `bcc`, `requiredattendees`, `optionalattendees`, `organizer`, `customers` and `resources` (the names are from memory,
checklist 1b), the states Open (status 1), Completed (2) and Canceled (3), and its relationships (regarding, notes,
parties, currency). Rules, each a 400 when broken:

- the primary column has the SchemaName `Subject` (logical name `subject`, without a publisher prefix);
- `HasActivities` is false and `HasNotes` true (both are set when absent; an explicit `HasNotes` false is refused, except in a
  solution import, where a `0` is turned into true with a warning);
- the owner is a user (`OwnershipType` UserOwned or absent): organization- and team-owned activities are refused;
- `ActivityTypeMask` is 0 or 1 (default 1) and cannot change afterwards; `IsActivity` cannot change either;
- `IsMailMergeEnabled` false and `IsAvailableOffline` true are the only accepted values.

Ways to create one:

```csharp
// SDK
service.Execute(new CreateEntityRequest
{
    Entity = new EntityMetadata
    {
        SchemaName = "xv_Sms", LogicalName = "xv_sms", IsActivity = true, OwnershipType = OwnershipTypes.UserOwned,
        DisplayName = new Label("Text message", 1033), DisplayCollectionName = new Label("Text messages", 1033),
    },
    PrimaryAttribute = new StringAttributeMetadata
    {
        SchemaName = "Subject", LogicalName = "subject", MaxLength = 200, DisplayName = new Label("Subject", 1033),
    },
});
```

```bash
# CLI (primary column subject, user owned, notes on)
xrmd table create xv_sms --label "Text message" --plural-label "Text messages" --activity
```

```yaml
# schema file, applied with xrmd apply
tables:
  - name: xv_sms
    label: Text message
    isActivity: true
    primaryName: { name: subject }
```

On the Web API, `POST EntityDefinitions` with `"IsActivity": true` and a primary attribute with `"SchemaName": "Subject"`;
in the web admin, the **Activity table** switch of the create-table dialog. A solution whose `customizations.xml` has
`<IsActivity>1</IsActivity>` creates the table the same way (see [Solutions](solutions.md)).

Deleting a custom activity table deletes its pointer and party rows with it, and removes it from the targets of the
lookups. The shared Activity privileges stay (see [Security](#security)).

## Party lists

A party list column holds the participants of one kind (`to`, `cc`, …). It has no column of its own in SQL: each
participant is a row of `activityparty` with the activity's id, the participant (`partyid`, an account, contact or
systemuser, and also a queue in the `from`, `to`, `cc` and `bcc` of an email), the address used (`addressused`, up to 320 characters) and the participation type (`participationtypemask`):

| Type | Meaning | Type | Meaning |
|---|---|---|---|
| 1 | Sender (`from`) | 8 | Regarding (kept by the server) |
| 2 | To recipient (`to`) | 9 | Owner (kept by the server) |
| 3 | CC recipient (`cc`) | 10 | Resource (`resources`) |
| 4 | BCC recipient (`bcc`) | 11 | Customer (`customers`) |
| 5 | Required attendee (`requiredattendees`) | 12 | Chat participant (refused) |
| 6 | Optional attendee (`optionalattendees`) | 13 | Related (`related`) |
| 7 | Organizer (`organizer`) | | |

**SDK.** A party list is an `EntityCollection` of `activityparty` entities, each with `partyid` (an `EntityReference`) or,
on `email` only, `addressused` alone (an address that names no record):

```csharp
var email = new Entity("email")
{
    ["subject"] = "Quarterly review",
    ["regardingobjectid"] = new EntityReference("account", accountId),
    ["to"] = new EntityCollection(new[]
    {
        new Entity("activityparty") { ["partyid"] = new EntityReference("contact", adaId) },
        new Entity("activityparty") { ["addressused"] = "someone@example.com" },
    }),
};
var id = service.Create(email);
```

A read (`Retrieve`, `RetrieveMultiple`, every page) returns each party list asked for as an `EntityCollection` whose
entities carry `activitypartyid`, `activityid`, `partyid` (with the record's name), `participationtypemask`, `addressused`
and `ispartydeleted`. A party list with no rows is left out of the record, as an empty column is.

**Web API.** Party lists are not properties of the entity type. Use the collection navigation
`{activity}_activity_parties` (`email_activity_parties`, `xv_sms_activity_parties`) in the body and in `$expand`:

```http
POST /api/data/v9.2/emails
{
  "subject": "Quarterly review",
  "regardingobjectid_account_email@odata.bind": "/accounts(<accountid>)",
  "email_activity_parties": [
    { "participationtypemask": 2, "partyid_contact@odata.bind": "/contacts(<contactid>)" },
    { "participationtypemask": 2, "addressused": "someone@example.com" },
    { "participationtypemask": 1, "partyid_systemuser@odata.bind": "/systemusers(<userid>)" }
  ]
}

GET /api/data/v9.2/emails(<id>)?$select=subject&$expand=email_activity_parties($select=participationtypemask,addressused,_partyid_value)
```

Each item takes `participationtypemask` (required), one `partyid_{table}@odata.bind` and `addressused`; any other property
is a 400. Inside the `$expand`, only `$select` is accepted. `$select` of a party list column (`$select=to`) is a 400. Any
other collection-valued navigation in a POST or PATCH body (a generic deep insert) is a 400.

**Rules on writes.**

- On Create, each party list sent is written as rows of its type. In the SDK, an Update with a party list **replaces the
  rows of its type only**; the other types stay, and an empty collection or null clears the type.
- On a Web API PATCH, `{activity}_activity_parties` is the activity's **whole party set**: every party list type is
  replaced by the items of its type in the array, a type with no item is cleared, and an empty array clears every party
  list. The Owner and Regarding rows stay (the server keeps them). Send every party you want to keep. That Dataverse treats
  the array this way is from memory (checklist 3a).
- `from` and `organizer` take at most one party. Types 8 and 9 are written by the server and refused when sent. A type the
  table does not have, and type 12, are refused. The same party twice in one list gives two rows.
- `partyid` must name an existing record of a table the column accepts: account, contact or systemuser, and queue for the
  `from`, `to`, `cc` and `bcc` of an email (404 `ObjectDoesNotExist` when missing; another table is a 400). **The caller must be able to read the party record** (403 otherwise); Append or AppendTo on it is not needed.
- An empty `addressused` is filled from the party: `emailaddress1` of an account or contact, `internalemailaddress` of a
  user, `emailaddress` of a queue.
- The server keeps one Owner row (type 9, the activity's owner, user or team) and, when there is a regarding record, one
  Regarding row (type 8) up to date on Create, Update and Assign. These two rows have no `addressused`.
- Deleting an activity deletes its party rows. Deleting a party record (an account, contact or queue) keeps its rows and sets
  their `ispartydeleted` to true.

**`activityparty` itself** is read-only: Create, Update and Delete of its rows are 400, through the SDK and the Web API.
Query it with `RetrieveMultiple` or link to it from an activity (`activityparty.activityid`) to filter by participant:
a party list column cannot be used in a condition, an order, a link or an aggregate (400), nor as a column of a link-entity.
A party row is readable when its activity is; no privilege on `activityparty` is needed.

## State and status

| Table | Open (0) | Completed (1) | Canceled (2) | Scheduled (3) |
|---|---|---|---|---|
| `email` | 1 Draft, 8 Failed | 2 Completed, 3 Sent, 4 Received, 6 Pending Send, 7 Sending | 5 Canceled | |
| `task` | 2 Not Started, 3 In Progress, 4 Waiting on someone else, 7 Deferred | 5 Completed | 6 Canceled | |
| `phonecall` | 1 Open | 2 Made, 4 Received | 3 Canceled | |
| `appointment` | 1 Free, 2 Tentative | 3 Completed | 4 Canceled | 5 Busy, 6 Out of Office |
| custom activity | 1 Open | 2 Completed | 3 Canceled | |

The default status of each state is the first one listed. A new activity starts in state 0 with that state's default
status (a task is Not Started). A status must belong to its state (400 otherwise).

- **Closed activities are read-only.** An Update of an activity in state 1 or 2 is a 400 (`InvalidArgument`) unless it only
  changes `statecode` and `statuscode` (a SetState). Reopen it first (state 0). Party lists and file uploads count as changes.
  Assign is the exception: a closed activity can be assigned.
- Moving an activity to state 1 or 2 sets `actualend` to now when it is empty. An activity created directly in state 1 or 2
  is accepted and gets `actualend` the same way.
- SetState arrives as an Update, as for every table (see [Plug-ins](plugins.md)).
- `activitypointer.statuscode` keeps the activity's own value; its formatted value comes from the option set of the
  activity's table (task status 3 shows "In Progress").

## SendEmail

`SendEmail` sends an email, or marks it as sent. Sending for real goes through the sender's mailbox and an email server
profile, set up as [Email and queues](email-and-queues.md#outgoing-email) describes.

- SDK: `SendEmailRequest { EmailId, IssueSend, TrackingToken }`, response `Subject`.
- Web API: `POST emails(<id>)/Microsoft.Dynamics.CRM.SendEmail` with `{ "IssueSend": true, "TrackingToken": "…" }`,
  answer `{ "Subject": "…" }`. The action is declared in `$metadata`.

What it does, in one transaction through the normal Update path (the pointer is updated and the change is audited as an
Update):

- the caller needs Write on the email;
- the email must be open (Draft or Failed), else 400; it needs at least one party in `to`, `cc` or `bcc`, else 400;
- the sender is the caller or a queue: an empty `from` is filled with the caller's own `systemuser`; a `from` naming a queue
  needs Read on that queue (403 otherwise); a `from` naming another user, a contact, an account or an address is a 400
  (sending on behalf of someone else is not modelled). The environment setting `xrmverse_EmailSendFrom` can allow queues only
  (see [Sender policy](email-and-queues.md#sender-policy-xrmverse_emailsendfrom));
- `IssueSend` false: state Completed, status 3 Sent, `senton` now; nothing leaves the server;
- `IssueSend` true: status 6 Pending Send, an empty `senton` (a Failed email's `senton` from an earlier attempt is
  cleared) and `deliveryattempts` 0, and one send job is queued. A recipient that is only an address is a 400 unless
  `organization.allowunresolvedpartiesonemailsend` is on. The job ends with status 3 Sent, or state 0 status 8 Failed with
  the reason on the job (see [Sending](email-and-queues.md#sending));
- `directioncode` becomes true, and `trackingtoken` is stored when one is given (an empty one counts as not given); with
  tracking tokens on, a token is taken and appended to the subject (see
  [Tracking tokens](email-and-queues.md#tracking-tokens));
- `IssueSend` is required (400 when missing or not a Boolean), and so is `EmailId`; an unknown email is 404.

Plug-in steps register on `SendEmail` with the filter `email` and get pre and post images of the email (see
[Plug-ins](plugins.md)). The `Update` steps of `email` do not fire for a send.

## Notes and attachments

A note is a row of `annotation`: `subject`, `notetext` (up to 100000 characters), `objectid` with `objecttypecode` (the record
it belongs to, any table with `HasNotes`), and an optional file: `documentbody` (base64), `filename`, `mimetype`, and the
read-only `filesize` and `isdocument`. On the Web API the record is bound with `objectid_{table}@odata.bind`.

```csharp
service.Create(new Entity("annotation")
{
    ["subject"] = "Contract",
    ["objectid"] = new EntityReference("account", accountId),
    ["filename"] = "contract.pdf",
    ["documentbody"] = Convert.ToBase64String(File.ReadAllBytes("contract.pdf")),
});
```

- **The body is never stored in SQL.** It is kept in the [file store](files-and-images.md) as a `fileattachment` row plus a
  blob, so backups, the cleanup sweep and the deletion queue cover it. A new body replaces the old one (the old blob is
  deleted after the commit, the new one if the transaction rolls back). Deleting a note, or the record it belongs to,
  deletes its body.
- **Size.** A body larger than `organization.maxuploadfilesize` (5 MB by default) is refused with `0x80044a02`
  (`AttachmentInvalidFileSize`), before it is decoded. Text that is not base64 is a 400.
- **What the server sets.** With a body: `filesize` (bytes), `isdocument` true, and `mimetype` from the file name's extension
  when you give none (`application/octet-stream` for an unknown extension). Without a body, or after `documentbody` is set to
  null or empty: `filesize` 0 and `isdocument` false; `filename` and `mimetype` stay. A client's `isdocument` is ignored.
- **Reads.** `Retrieve`, `RetrieveMultiple`, all columns, link-entities, plug-in images and the Web API return the base64
  text. Only the notes a query returns get a body, so a body is never readable where its note is not. `documentbody` cannot
  be filtered, sorted, aliased, joined on or aggregated (400).
- **Read cap.** One query reads at most `Files:MaxNoteBodyBytesPerRead` bytes of bodies (64 MB by default, see
  [Configuration](configuration.md)). A page whose bodies add up to more is refused with a 400 that names `documentbody`,
  before any body is read: leave `documentbody` out of the columns, or read fewer notes per page.
- `InitializeAnnotationBlocksUpload` and `InitializeAnnotationBlocksDownload` (chunked transfer) are refused, so a body is
  written and read in one piece. The file messages (`InitializeFileBlocksDownload`, `DeleteFile`) refuse a note body with a
  400 that says to use an Update.

## Posts

A **post** is a short plain-text message about one record, with replies (**comments**) and @mentions of users; the
timeline shows them when an app turns them on (see [Model-driven apps](model-driven-apps.md#posts)). The tables follow
Learn's table references of [post](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/post),
[postcomment](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/postcomment) and
[postregarding](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/postregarding), read and
paraphrased. Every organization is seeded with them (the type codes and privilege names are from memory):

| Table | Entity set | Type code | Owner | What it holds |
|---|---|---|---|---|
| `post` | `posts` | 8000 | organization | posts; primary name `text` |
| `postcomment` | `postcomments` | 8005 | organization | the replies to a post; primary name `text` |
| `postregarding` | `postregardings` | 8002 | organization | one row per record that has posts, kept by the server |

**Columns.**

- `post`: `text` (2000 characters, required), `largetext` (memo, not shown by the timeline), `regardingobjectid` (required,
  any table with notes or activities that is not an intersect table) with `regardingobjecttypecode`, `source` (choice
  `post_source`: 1 Auto Post, 2 Manual Post, 3 ActionHub Post; default 2), `type` (choice `post_type`: 1 Check-in, 2 Idea,
  3 News, 4 Private Message, 5 Question, 6 Re-post, 7 Status; default 1; any value is accepted), the read-only
  `regardingobjectownerid`, `regardingobjectowneridtype` and `regardingobjectowningbusinessunit` (copied from the record
  when the post is created), `postregardingid` (set by the server, left out of reads) and the system columns, with
  `organizationid` and, under impersonation, `createdonbehalfby`. The Yammer columns are not modelled.
- `postcomment`: `text` (1000 characters, required), `largetext`, `postid` (required), `createdby`, `createdon`,
  `createdonbehalfby` and `organizationid`. It has no `modifiedon` or `modifiedby`.
- `postregarding`: `regardingobjectid`, `regardingobjecttypecode`, the three regarding owner columns and
  `latestmanualpostmodifiedon`, which the server moves forward to the `createdon` of each new post.

**Relationships.** `Post_Comments` (post to postcomment) and `post_PostRegardings` (postregarding to post) cascade Delete,
and so does `{Table}_PostRegardings` from each target table to `postregarding`. So deleting a post deletes its comments, and
deleting a record deletes its `postregarding` row, its posts and their comments; plug-in steps on the Delete of `post` and
`postcomment` fire for each. `post.regardingobjectid` has the same targets but no saved relationship, as on Learn; on the
Web API it binds per target (`regardingobjectid_account@odata.bind`). A table cannot be deleted while posts are about its
records: "Entity '{name}' cannot be deleted: N posts are regarding its records. Delete them first."

**Messages.** `post` and `postcomment` support Create, Retrieve, RetrieveMultiple and Delete, through the Web API, the SDK,
SOAP and FetchXML, and plug-ins and webhooks see their Create and Delete. Refused with a 400 (`0x80040203`). The first three
rows are raised before any privilege check, and for every caller except that the server itself (`Caller.Internal`) may write
`postregarding`; the two missing-lookup rows are raised only after `prvCreatePost` has been checked:

| Request | Message |
|---|---|
| Update of a `post` or `postcomment` (also an Associate or Disassociate that changes their lookups) | Posts and comments cannot be updated |
| Create, Update or Delete of a `postregarding` | postregarding is maintained by the server |
| Create of a `post` with `source` 1 or 3 (also when a plug-in step sets it) | Auto posts are not supported in this version |
| Create of a `post` without `regardingobjectid` | A post needs 'regardingobjectid': the record it is about. |
| Create of a `postcomment` without `postid` | A postcomment needs 'postid': the post it replies to. |

**Security.**

- The privileges are `prvCreatePost`, `prvReadPost` and `prvDeletePost`, at Global depth (the tables are organization-owned).
  `postcomment` and `postregarding` use the same three. There is no Write, Append, AppendTo, Assign or Share privilege.
  Basic User holds the three in new organizations and, through a one-time top-up at startup (`post-privileges`), in
  existing ones; System Administrator holds them too.
- **Read rule (our call).** On top of `prvReadPost`, a `post` or `postregarding` row is readable only when the user can read
  its regarding record, and a `postcomment` only when its post is readable. Retrieve answers 403; RetrieveMultiple, `$count`
  and FetchXML aggregates leave the rows out, so counts never reveal a record the user cannot read.
- Creating a post needs `prvCreatePost` and read on the regarding record; creating a comment needs `prvCreatePost` and read
  on its post. No Append or AppendTo is asked for.
- Deleting a post or comment needs `prvDeletePost` **and** read on its regarding record (or post): stricter than the
  privilege alone, so a user cannot delete by id a post they cannot see.

**Mentions.** A mention is written inside `text` in the format Learn documents in
[Introduction to activity feeds](https://learn.microsoft.com/dynamics365/customerengagement/on-premises/developer/introduction-activity-feeds?view=op-9-1):
`@[Entity,ID,"Display String"]`, with no space after the commas, where Entity is an object type code or a logical name. The
timeline writes a user as `@[8,{a1b2c3d4-...},"Full Name"]` (type code 8, the id braced and in lower case) and reads both
`8` and `systemuser` as a user. The server stores the text as written and does not check the mentions.

**Not migrated.** `post`, `postcomment` and `postregarding` are in the "not supported yet" tables of the data migration
(see [Migration](migration.md)).

## Security

- **Shared privileges.** Every activity table, custom ones included, and `activitypointer` use one privilege set:
  `prvCreateActivity`, `prvReadActivity`, `prvWriteActivity`, `prvDeleteActivity`, `prvAppendActivity`,
  `prvAppendToActivity`, `prvAssignActivity`, `prvShareActivity`. Notes use `prvCreateNote` … `prvShareNote`. There are no
  per-table privileges such as `prvReadEmail` (per-table privileges are issue
  #66). `activityparty` has no privileges.
- **Basic User** holds the eight Activity and the eight Note privileges at Basic (User) depth, in new organizations and,
  through a one-time top-up at startup, in existing ones.
- **Reading through the pointer.** A pointer row carries the owner columns of its activity, so owner depth, business units
  and hierarchy security apply with `prvReadActivity`. A share or an access team on an activity opens its pointer row too
  (matched by id and by the activity's type). A table left out of hierarchy security is left out through the pointer as well.
- **Shares are per table.** Sharing, revoking or deleting an activity reads and changes only the shares of that activity,
  never those of a record of another table that has the same id (see [Security](security.md); migration
  `0011_share_object_type.sql` makes the share index per table on an existing database).
- **Party rows** are readable exactly when their activity is. Writing a party needs Read on the party record (see above).
- **Field security.** The common activity columns of every activity table, and every column of `activitypointer`, cannot be
  secured (400 `FieldNotSecurable`): the pointer copy would show a secured value. Neither can `annotation.documentbody`. Your
  own columns on an activity table can be secured as usual.
- **Auditing.** Activities and notes are audited like any table. Party list changes and note bodies are not written to the
  audit; the pointer and party writes are not audited on their own. `SendEmail` is audited as an Update of the email.
- **Change tracking** can be switched on for the activity tables and `annotation` (a new body moves the note's version), but
  not for `activitypointer` or `activityparty` (400).

## What is refused

These messages are answered with a 400 that says why (through the SDK, SOAP and the Web API, unbound or bound), and no
custom API or custom action can take their names: `Book`, `Reschedule`, `AddRecurrence`, `DeliverPromoteEmail`,
`DeliverIncomingEmail`, `CheckIncomingEmail`, `CheckPromoteEmail`, `BackgroundSendEmail`, `SendEmailFromTemplate`,
`SendTemplate`, `InstantiateTemplate`, `Rollup`, `Restore`, `AppendRelatedParty`,
`InitializeAnnotationBlocksUpload` and `InitializeAnnotationBlocksDownload`. The queue messages run (see [Email and queues](email-and-queues.md)).
An `sdkmessage` row with one of these names does not make it run. These names are also reserved as entity set names, so a new table, or a table in a solution import, whose entity set is named `Rollup`, `Book` and so on is a
400.

`InitializeAttachmentBlocksUpload` and `InitializeAttachmentBlocksDownload` are refused too: an email attachment's body (`activitymimeattachment.body`, base64) is written with Create or Update and read with Retrieve, like a note's `documentbody`, and lives in the file store. The table follows its email: read needs read on the email, create, update and delete need write on it, and the email must be open to add or change attachments (deleting one is not refused on a sent email: checklist 5); `email.attachmentcount` follows the rows and `organization.blockedattachments` and `maxuploadfilesize` are checked.

The tables `letter`, `fax`, `recurringappointmentmaster`, `socialactivity`,
`template`, `serviceappointment` and `campaignactivity` are not seeded: they give the usual "entity not found" 400.

## Surfaces

Where you manage activities and notes:

- **CLI.** `xrmd table create --activity`, `--notes` and `--activities`, `xrmd table set <table> --notes|--activities`, and
  `xrmd table describe` shows the three settings (see [CLI](cli.md#tables-plug-ins-data-and-schema-files)). `data export`
  and `dev seed` skip party list columns with a notice, and `data import` of a file that names one exits 2.
- **Schema files.** `isActivity`, `hasNotes`, `hasActivities` (see [CLI](cli.md#tables-plug-ins-data-and-schema-files)).
- **Web admin.** The **Activity table** switch when creating a table, the **Notes** and **Activities** switches on a table's
  page, party lists shown read-only on a record form (see [Web admin](web-admin.md#activities-and-notes)).
- **Model-driven apps.** Party lists are edited on activity forms, and an email form has **Send**, the send status and the
  attachments (see [Model-driven apps](model-driven-apps.md#email-form)).
- **MCP.** The record tools work on activities and notes; party lists are left out of the records they return.
- **Solutions.** See [Solutions](solutions.md).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **Posts.** The read rule through the regarding record is our call (Learn does not say how Dataverse checks it), and it
  also applies to Delete. `RetrievePrincipalAccess` on a post reports the privileges only, without the read rule. An
  explicit `$select` (or ColumnSet) of `post.postregardingid` still returns it, while Dataverse does not let it be read.
  Auto posts, post rules and configurations, likes and follows, attachments on posts, `#` record references, the
  personal wall, mention notifications and rich text are not supported (see
  [Known limits of the timeline](model-driven-apps.md#known-limits-of-the-timeline-ui-1g)).
- **Stored twice.** The common columns, `description` included, are kept in the activity's table and in `activitypointer`.
  A `subject` longer than 400 characters (possible on `email`) is cut to 400 in the pointer, and a `description` longer than
  2000 characters (possible on `email`, whose `description` is far longer) is cut to 2000, the pointer's declared length;
  what Dataverse's pointer holds in both cases is on the checklist.
- **Not modelled.** Team-owned and organization-owned custom activities; the party list `partners` and any other party
  column not listed above; `knowledgearticle` records as parties; a queue as a party outside the `from`, `to`, `cc` and `bcc` of
  an email; an address without a record outside `email`;
  inline (content id) attachments; recurring appointments; any standard column outside the lists above.
  `activityparty` has a `statecode` and `statuscode` like every xrmverse table, which Dataverse's does not.
- **Sending email.** The sender is the caller or a queue the caller can read (no send-as another user, no delegate access).
  The limits of outgoing email are in [Email and queues](email-and-queues.md#known-limits). The plug-in message is named
  `SendEmail`; whether Dataverse names it `Send` for plug-ins is on the checklist. The `Update` steps of `email` do not fire on
  a send, and the send is audited as an Update.
- **Party reads and writes.** Writing a party needs Read on the party record (stricter than Dataverse may be). A reader of an
  activity sees each party's name and address even without Read on the party record. An empty party list is left out of a
  read. A party list cannot be a link-entity column. The Owner and Regarding rows have no `addressused`. The Owner row of a
  team-owned activity has no `associatednavigationproperty` annotation on the Web API (`team` is not a `partyid` target).
  A Web API PATCH with `{activity}_activity_parties` replaces the whole party set (a type left out is cleared, an empty
  array clears them all), while an SDK Update replaces only the types it sends (checklist 3a).
- **`$expand`.** The per-target navigations (`regardingobjectid_{table}`, `regardingobjectid_{table}_{activity}`,
  `activityid_{activity}`, `partyid_{table}`) expand one level, with no nested `$expand`. Other multi-target lookups
  (Customer, Owner) still cannot be expanded. `fileattachment` lists its `objectid` navigation in `$metadata` for its first
  target only, so `objectid_annotation` is declared only where `annotation` is that target (a new organization).
- **Notes.** A body is at most `maxuploadfilesize` bytes (up to 131,072,000, which is 125 MiB). That is a different limit
  from the 128 MB (134,217,728 bytes) a single request may carry: the body travels base64-encoded, about a third larger, so
  a body near the maximum does not fit in one request, and there is no chunked transfer. One query reads at most
  `Files:MaxNoteBodyBytesPerRead` bytes of bodies. `GET annotations(<id>)/documentbody` is a 400 (the property routes serve
  file and image columns only). Plug-in images and all-columns reads read the body from the store each time. A body deleted
  by another request between the row read and the blob read gives 404.
- **Field security.** The common activity columns and `annotation.documentbody` cannot be secured.
- **Audit and change tracking.** Party list changes and note bodies are not audited; `activitypointer` and `activityparty`
  cannot be tracked.
- **Deleting a table** with notes, or with activities regarding its records, is refused (no cascade), also in a managed
  uninstall and its dry run.
- **Cascades.** Assign, Share, Unshare, Reparent and Merge of a regarding or notes relationship are stored, not enforced
  (the general gap of [Solutions](solutions.md)).
- **Refused names.** A bound refused action (`POST {set}({id})/Microsoft.Dynamics.CRM.Book`) answers 400 for any entity set
  segment, an unknown one included, where 404 would be expected.
- **Error codes.** Every refusal of this page is `InvalidArgument` (`0x80040203`, 400) with xrmverse's own text, except a
  missing party or regarding record (404 `0x80040217`), a body over the limit (`0x80044a02`) and the security errors. The
  checklist records what Dataverse answers.
- **From memory.** The object type codes, the custom activity party lists and states, the relationship names other than
  `account`'s and the parties', the Basic User depths, the range of `maxuploadfilesize` and several behaviours above are
  not verified on a real Dataverse.

## See also

- [Supported queries](queries.md#activities-party-lists-and-note-bodies)
- [Security](security.md#activities-and-notes)
- [Model-driven apps](model-driven-apps.md): the timeline
- [Files and images](files-and-images.md)

# Email and queues

[← xrmverse documentation](../README.md#documentation)

## Introduction

A **queue** is a shared list of work. A record that someone must handle (an email, a task, a phone call, an appointment or a
row of a custom activity table) is put in a queue as a **queue item**; a worker **picks** the item, works on it, and either
**releases** it back or removes it. Queues are what a contact center or a shared mailbox is built on: customers' messages
arrive in a team queue, and agents take them one by one. xrmverse follows the Dataverse model of tables, messages and error
codes for queues. Whether each detail matches a real Dataverse is not verified (see
[Known limits](#known-limits)).

This page covers queues, which arrived with roadmap item 4.1a. Sending and receiving email through mailboxes (4.1b and 4.1c)
is not part of it: `SendEmail` still only changes the state of the email (see [Activities and notes](activities.md#known-limits)).

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
| Email sending and receiving | Not part of 4.1a | **Narrower** (planned in 4.1b and 4.1c) |

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

A `queueitem` links one record (`objectid`, with the table in `objecttypecode`) to one queue (`queueid`). It also holds the
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

### Use the command line

| Command | Does |
|---|---|
| `xrmd queue list [--public]` | Lists queues with their visibility and state; `--public` shows only public ones |
| `xrmd queue create <name> [--private]` | Creates a queue, public unless `--private` |
| `xrmd queue member add <queue> <user\|team>` | Adds a user or a team; the queue is named by name or id |
| `xrmd queue items <queue>` | Lists the items of a queue |

An unknown queue, user or team exits 4 (not found); a name that matches several queues exits 5 (use the id). Other refusals
are the server's (see [CLI](cli.md#exit-codes)).

## Surfaces

- **Model-driven apps.** The list command bar has **Add to queue** on queue-enabled tables, and **Pick**, **Release**,
  **Route** and **Remove from queue** on the queue item grid (see [Model-driven apps](model-driven-apps.md)).
- **CLI.** `xrmd queue` (see [CLI](cli.md)).
- **Web API, SDK, SOAP.** The messages above.
- **Web admin.** Queues are tables like others; there is no queue-specific page. A table's page has a one-way switch to
  enable queues for the table (`IsValidForQueue`).

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
- **No email transport.** Mailboxes, sending and receiving email are roadmap items 4.1b and 4.1c.

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

## See also

- [Activities and notes](activities.md)
- [Model-driven apps](model-driven-apps.md)
- [CLI](cli.md)
- [Security](security.md)
- [Plug-ins](plugins.md)
- [Known issues and limitations](known-issues.md)

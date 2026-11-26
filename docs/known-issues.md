# Known issues and limitations

[← xrmverse documentation](../README.md#documentation)

**As of November 25, 2026.** This page lists, in one place, the known issues of xrmverse, the limitations of the current
version and the deliberate divergences from Dataverse. The Status column says whether each entry is planned to change or whether it stays.
The topic pages have the details; [xrmverse compared with Dataverse](dataverse-comparison.md) gives the feature-by-feature
view.

xrmverse follows one rule: what it cannot reproduce faithfully fails explicitly (an error that names what is not
supported), and never diverges from Dataverse in silence. Most entries below are therefore refusals, not wrong results.

**Status** (third column of every table):

- **Planned**: on the roadmap ([README](../README.md#roadmap)), with the item that brings it.
- **To be fixed**: a known issue that will be fixed; not yet scheduled.
- **Not scheduled**: possible in the future, with no plan today.
- **Stays**: a deliberate difference, by design.

The page is kept up to date: every pull request that adds, changes or removes a limitation or a known issue updates it
(see [Keeping the documentation aligned](../README.md#keeping-the-documentation-aligned)).

## Known issues

Behaviour that is wrong today and will be fixed.

| Issue | Description | Status |
|---|---|---|
| Empty columns are left out of a `$select` response (#168) | The Web API leaves out a selected column whose value is null, while Dataverse returns it with `null`. A client that tells "empty" from "not readable" by the presence of the property (for example a secured column on a form, which shows "No access") gets it wrong. | To be fixed |
| Date-only columns returned with a time (#232) | The Web API returns a column with the `DateOnly` behavior as `2026-09-10T00:00:00Z`, while Dataverse returns `2026-09-10`. The date is right; a client that expects an `Edm.Date` fails to parse it. | To be fixed |
| Empty currency filled on create (#231) | A record created without `transactioncurrencyid` gets the base currency and an exchange rate of 1; a record migrated from Dataverse with an empty currency therefore has one in xrmverse. | To be fixed |
| `like` on a lookup column (#107) | A FetchXML `like` (or begins with, ends with) condition on a lookup is refused with a 400, while Dataverse matches it against the related record's name. Quick find views that list a lookup among their find columns show the error. | To be fixed |
| Deadlock under parallel activity creates (#90) | Many activities created in parallel can make SQL Server pick one request as a deadlock victim: the caller gets 500 `0x80044150` and must retry (the `xrmd` CLI retries by itself). | To be fixed |
| Associate and Disassociate on registration tables (#89) | Associate and Disassociate over the lookups of plug-in step registration rows (plug-in type, message, filter) skip the checks that Create and Update apply. Webhook and plug-in type relationships are already refused. | To be fixed |
| Server version after an upgrade (#223) | The server image reports version `1.0.0` instead of the release version, so `xrmd server status` shows a different version from the image tag and the CLI. | To be fixed |
| No default forms and views for `contact` and `account` (#169) | A new organization has no forms and views for `contact`, and some standard columns of `account` and `contact` are missing. Apps that rely on Dataverse's standard forms, views and columns without shipping them cannot open these records. | To be fixed |
| No default form for custom activity tables (#195) | A custom activity table has no default main form: it opens in a model-driven app only with a form that its solution brings or one made in the form designer. The standard activity tables `email`, `task`, `phonecall` and `appointment` have a default main form and views (see [Email and queues](email-and-queues.md#default-forms-and-views)). | To be fixed |
| Two default forms after importing a solution | A solution imported after the server has seeded its forms and views (email, queue, queue item, task, phone call, appointment) can bring its own default main form or default public view for the same table. The import keeps both as default; an app with no form components then picks one of the two by order and name. Clear the default on the unwanted one in the web admin. The server's own seed never adds a second default. | To be fixed |
| System tables that plug-ins expect (#172) | Some tables and columns that real plug-ins read do not exist in xrmverse (for example `powerpagecomponent` and `systemuser.accessmode`): such a plug-in fails with an explicit "not found" error. | To be fixed |
| Site map and list page details (#170, #210) | A list shows the title of the wrong subarea when two subareas share a table; custom table icons (`IconVectorName`) are not shown in the site map. | To be fixed |
| Ribbon messages in English only (#186) | The warnings about the ribbon and the reasons a command is unavailable are not translated. | To be fixed |
| `appmoduleroles` columns (#163) | The table that links apps and roles lacks the lookup, owner and solution columns that Dataverse has. | To be fixed |
| Managed processes can be changed through the authoring API (#310) | `POST /authoring/workflows/apply` (and so `xrmd workflow apply` and the workflow studio) does not refuse a process that belongs to a managed solution, while Dataverse does not let a managed component be changed in place. The studio only shows a **Managed** badge. | To be fixed |
| Lookup values in a loaded view filter (#261) | In the web admin's view designer, a lookup condition of a saved view shows the record's id instead of its name; a record picked in the designer shows its name. The filter itself is right. | To be fixed |
| Deleting a file while the record is updated | `DeleteFile` on a file or image column and an Update that clears the same column at the same moment can deadlock: one request fails with 500 `0x80044150` and can be retried. On SQL Server and PostgreSQL alike. | To be fixed |
| Queue counters | `queue.numberofitems` and `queue.numberofmembers` are not maintained. | To be fixed |
| Deleting a queue while an item is added | Deleting a queue at the moment an item is being added to it can remove that just-added item. | To be fixed |
| Interrupted email send stays Pending Send | A send job interrupted after it started an attempt (the server stopped, for example) is not run again, so that the email is never sent twice: the email stays Pending Send and must be checked and sent again by hand. See [Email and queues](email-and-queues.md#known-limits). | To be fixed |
| Temporary recipient error fails the mailbox | A temporary error about one recipient (for example SMTP `452`) that is still there after the last retry sets the sender's mailbox to Failure, not only the email. | To be fixed |
| Reopening and sending again an email whose send is in progress can deliver it twice | When an email that is Pending Send is reopened and sent again while its first send job is handing it to the server, both jobs can send it, and the email is marked Sent. See [Email and queues](email-and-queues.md#known-limits). | To be fixed |
| Graceful stop during a send leaves the email Pending Send | When the server stops gracefully while a send job prepares the message, before the message is handed to the mail server, the job ends as interrupted and is not run again: the email stays Pending Send although nothing was sent. Reopen the email, then send it again. See [Email and queues](email-and-queues.md#known-limits). | To be fixed |
| `xrmd envvar list` with long values (#177) | Long JSON values make the table output hard to read; use `--output json`. | To be fixed |

## Limitations

Features that are missing or partial today. Each one fails explicitly where it is used.

### Columns, queries and the Web API

| Limitation | Description | Status |
|---|---|---|
| Rollup column jobs on large tables | The hourly incremental job aggregates every row of the table for each rollup, and a mass run is one `UPDATE` over the whole table in one transaction, which can block writers. The background worker runs one job at a time, so a long run delays other queued jobs. See [Rollup columns](rollup-columns.md#known-limits). | Not scheduled |
| Rollup jobs: `rollupfield`, pause and resume | The `rollupfield` table is not modelled (rollup jobs have no `regardingobjectid`, a query on `rollupfield` fails as an unknown table), and rollup jobs cannot be paused, resumed, cancelled or deleted; Postpone is supported. See [Rollup columns](rollup-columns.md#known-limits). | Stays |
| Rollup values not yet compared with Dataverse | Empty sets, `AVG`, Currency conversion, filters and the hierarchy match a licensed environment; Decimal truncation, `AVG` rounding, overflow and inactive rows follow Learn and are still to be checked. See [Rollup columns](rollup-columns.md#known-limits). | To be fixed |
| Currency rollups keep four decimals | Money is stored with four decimals, so a Currency rollup holds 63.3333 where Dataverse shows 63.333332, and an empty set writes a `_base` of 0 where Dataverse leaves it empty. See [Rollup columns](rollup-columns.md#known-limits). | Not scheduled |
| Notifications in the modern look | Toasts (notifications) use the page-wide theme, not the colours of the modern look's presentation overlay. The same holds in the web admin when it follows a dark environment look: toasts keep the page-wide theme. | To be fixed |
| Choice formula columns | A formula column of type Choice is refused (`Choice formula columns are not supported yet`): the rules Dataverse uses to bind a formula to a choice are not shown by any export. Currency formula columns do not exist in Dataverse either. See [Formula columns](formula-columns.md#known-limits). | Not scheduled |
| Error functions in formula columns | `Error`, `IfError` and `IsError` are refused in a formula. An error (a division by zero, an out-of-range result) propagates as in Power Fx and shows as an empty value, but a formula cannot test for it or replace it. See [Formula columns](formula-columns.md#blank-values-and-errors). | Not scheduled |
| Formulas in the caller's language | Dataverse's Web API reads a `FormulaDefinition` with the calling user's separators (an Italian user writes `;` between arguments) and stores it in the invariant form, with `,`. xrmverse reads the invariant form only, for every caller: a formula with `;` separators or a `,` decimal mark is a 400 (for `;`, with a hint to use `,` between arguments and `.` as decimal mark), never read another way. Not scheduled because the invariant form is what Dataverse stores, returns and exports, so solution files, schema files and formulas copied from `RetrieveAttribute` all work; only a formula typed in a non-English user's own notation needs rewriting. See [Formula columns](formula-columns.md#defining-a-formula-column). | Not scheduled |
| Formula columns not yet compared with Dataverse (#20) | The parity export (three formulas, the file format) and a run of eleven formulas on a licensed environment (2026-11-19: blank operands, errors in arithmetic, `Blank() = 0`, `Round`, `Mod`, `Trim`, `Text` padding, a related column, `Weekday` on a User local date, the leading `=`, the read-back of `FormulaDefinition`, `SourceTypeMask`; then a formula column reading a failed one, which reads it as blank, and `Max` with blank arguments, which leaves them out) match. Errors in conditions, `IsBlank` and text functions, `Max`/`Min` of blanks only, the other `Text` formats and date functions, and the error codes follow Learn and Power Fx and are still to be checked. See [Formula columns](formula-columns.md#known-limits). | To be fixed |
| Whole number formula results are truncated | A formula column of type Whole number keeps the whole part of a decimal result (`7 / 2` gives 3) instead of rounding it. See [Formula columns](formula-columns.md#types-and-results). | To be fixed |
| Text comparisons in formula columns | `=`, `<>`, `in`, `StartsWith` and `EndsWith` in a formula compare text with the database collation: case-insensitive with the default collation, and `=` ignores trailing spaces, while Power Fx's `=` is case-sensitive (not yet compared with Dataverse, #20). See [Formula columns](formula-columns.md#types-and-results). | To be fixed |
| Date parts of a Time zone independent date in formulas | `Day`, `Weekday`, `Hour` and the other date parts are refused on a Time zone independent date by the Power Fx type check, with its own message, and on a User local date by xrmverse; they work on Date only dates. See [Formula columns](formula-columns.md#dates). | To be fixed |
| `DateAdd` with a fractional count | In a formula, `DateAdd` truncates a fractional count to a whole number (`1.5` days adds one day), where Power Fx adds the fraction. | To be fixed |
| `DateAdd` with a count beyond a whole number | In a formula, `DateAdd` with a count outside ±2,147,483,647 (`DateAdd(d, 1e11, TimeUnit.Milliseconds)`) is an error (an empty value), where Power Fx gives a date. See [Formula columns](formula-columns.md#dates). | To be fixed |
| `Value` of text in formulas | `Value` and `Decimal` read plain decimal numbers only: `"1e3"`, `"1,000"` and `"50%"` are errors (an empty value), while `Float("1e3")` gives 1000. | To be fixed |
| `Max` and `Min` in formulas need SQL Server 2022 | They use `GREATEST` and `LEAST`, which older SQL Server versions lack; the bundled image is SQL Server 2022. | Stays |
| `Trim` in formulas and U+0001 | `Trim` in a formula column also removes any U+0001 control character from the text. See [Formula columns](formula-columns.md#known-limits). | To be fixed |
| Collection-valued navigation properties (#88, #202) | One-to-many and many-to-many navigation properties are not in `$metadata`, cannot be expanded and cannot be used as a navigation path, except the activity parties of an activity. Single-valued `$expand`, nested at any depth, works. See [Supported queries](queries.md#known-deviations). | Not scheduled |
| Hierarchy functions in `$filter` (#50) | `Microsoft.Dynamics.CRM.Under`, `Above`, `EqUserOrUserHierarchy` and the other hierarchy query functions are refused with 400 in the Web API. The same operators work in FetchXML and QueryExpression. | Not scheduled |
| Update of one column with `PUT` | The Web API does not support `PUT <entity set>(<id>)/<column>` to set a single column; use `PATCH` on the record. | Not scheduled |
| `EntityDefinitions` and relationships | `$expand` on `EntityDefinitions` accepts only `Attributes`; `ManyToOneRelationships`, `OneToManyRelationships` and `ManyToManyRelationships` are refused with 400. Read relationships with `RelationshipDefinitions` or `RetrieveEntity`. | Not scheduled |
| Metadata properties xrmverse does not model | `CreateEntity`, `CreateAttribute` and the Web API equivalents refuse a table or column setting that xrmverse does not store (unless it has the value an export would write). `UpdateEntity`, `UpdateAttribute` and `UpdateOptionSet` change only what xrmverse applies (labels; for columns also the required level, `IsSecured`, a larger `MaxLength` and the options; for global choices the labels and options); a request that changes anything else is refused with 400 naming the properties. Settings are added as features need them. See [Solutions](solutions.md). | Stays for settings without an effect in xrmverse |
| Paging with one-to-many links | A query is paged with the paging cookie (keyset on the primary key) only when it is sorted on the primary key and its links are many-to-one; otherwise pages are read by page number, so rows written between two pages can be skipped or repeated. See [Supported queries](queries.md). | Not scheduled |
| Query size limits | At most 32 nested filters, 10 levels of link-entity and 2000 SQL parameters per query; beyond that, 400. Dataverse does not state these limits. | Stays |
| Dates in queries | Dates are evaluated in UTC, also in `dategrouping`; ordering by a lookup sorts by id, not by name. See [Supported queries](queries.md#known-deviations). | Stays |
| Personal formats: dates only | Each user picks the date and time formats in Personalization; there is no region list and no number, currency or long date format, and the time zone is set through the Web API or the SDK. Aggregate values grouped by date, audit history and the SQL endpoint show dates in en-US and UTC. See [Model-driven apps](model-driven-apps.md#known-limits-of-time-zones-and-date-formats-131). | Not scheduled |
| Elastic tables in solutions (#77) | An elastic table cannot be exported in a solution. See [Elastic tables](elastic-tables.md#known-gaps). | Not scheduled |

### Solutions and metadata

| Limitation | Description | Status |
|---|---|---|
| Solution patches | A patch is refused at import, and `CloneAsPatch` and `CloneAsSolution` always return 400. See [Solutions](solutions.md). | Not scheduled |
| Export of a managed solution | `ExportSolution` exports unmanaged solutions only; asking for a managed export is a 400. Managed solutions can be imported, upgraded and uninstalled. | Not scheduled |
| Export into Dataverse not yet verified (#20) | A solution exported by xrmverse is checked against its own import, not yet against an import into a real Dataverse environment. | Not scheduled |
| Microsoft first-party solutions | Solutions from Microsoft (Sales, Customer Service and similar) depend on components xrmverse does not have. Their import is not blocked but logs a warning, and they are not supported. | Stays |
| Assign, Share, Unshare, Reparent and Merge cascades | These cascade settings of a relationship are stored, imported and exported, but not applied: only Delete cascades run. See [Solutions](solutions.md). | Not scheduled |
| `xrmd solution delete --cascade` has no dry run | The cascade delete of an unmanaged solution (which drops its tables and their data) asks for confirmation but cannot preview what it removes. `xrmd solution uninstall --dry-run` exists for managed solutions. | Not scheduled |
| Publisher checks | Unique publisher names and prefixes, and the prefix format, are checked by the `xrmd` CLI, not by the server for direct creates or solution import (#27). | To be fixed |
| Environment variable values as their own component (#176) | Values travel with their definition, not as a separate solution component (type 381). See [Environment variables](environment-variables.md#known-limits). | Not scheduled |
| Option value parameters | `InsertOptionValue` and `UpdateOptionValue` refuse `ParentValues`, `ExternalValue` and `IsHidden` `true` with a 400 naming them (dependent options, external values and hidden options are not modelled); `UpdateStateValue` is not supported. See [Rules for metadata writes](solutions.md#rules-for-metadata-writes). | Not scheduled |
| Setting environment values in solutions | The environment values of app settings (`organizationsetting` rows, for example the environment's default look) are not exported or imported with a solution; app values (`appsetting`) and web resources are. Set the environment values again in each environment. See [Solutions](solutions.md). | Planned |

### Security and identity

| Limitation | Description | Status |
|---|---|---|
| Disabled business units | `businessunit` has no `isdisabled` column: a business unit cannot be disabled. | Not scheduled |
| Roles per business unit | Roles exist only in the root business unit and are assigned to users and teams of any business unit; there are no per-business-unit copies. See [Security](security.md). | Stays |
| Content of the standard roles | System Administrator, System Customizer, Basic User and Delegate exist with their Dataverse template ids; the privileges of System Customizer and Basic User are xrmverse's own choice, because xrmverse does not have Dataverse's standard tables. Other Dataverse standard roles (for example Salesperson) do not exist. | Stays |
| Per-activity-table privileges (#66) | All activity tables share the `prv{Verb}Activity` privileges. See [Activities and notes](activities.md#known-limits). | Not scheduled |
| Power Platform CLI (`pac`) with the local identity provider | `pac` signs in only with Microsoft Entra, so it does not work with an organization that uses xrmverse's own identity provider; use `xrmd solution import/export`. See [Local identity provider](local-idp.md). | Stays |
| SQL endpoint sign-in | The read-only SQL endpoint accepts only SQL logins of the local identity provider; organizations that sign in with Entra have no SQL endpoint for now. See [SQL endpoint](sql-endpoint.md#known-limits). | Not scheduled |
| SQL endpoint language | A subset of read-only T-SQL (no subqueries, `UNION`, `HAVING`, `CASE`, `CAST`, scalar functions in the select list, `sys.*` views); results are buffered and capped at 50,000 rows by default. | Stays |

### Pipeline, workflows and integrations

| Limitation | Description | Status |
|---|---|---|
| Service endpoints other than webhooks (#84) | Only webhooks (contract 8) are supported; Azure Service Bus queues and topics and Event Hubs are refused at registration. See [Webhooks](webhooks.md#known-limits). | Not scheduled |
| Webhook ports | A webhook URL must use port 80 or 443 unless `Pipeline:WebhookAllowAnyPort` is `true`, which covers tests and on-premises receivers. | Stays |
| SMTP and IMAP without TLS | An SMTP or IMAP server that offers no STARTTLS is refused before any credential is sent, unless `Mail:AllowInsecureSmtp` (SMTP) or `Mail:AllowInsecureImap` (IMAP) is `true`, settings for test and development servers. See [Email and queues](email-and-queues.md#configuration). | Stays |
| Webhooks in solutions (#85) | Webhooks and their steps are registered per organization and are never part of a solution. | Not scheduled |
| Webhook authentication values (#86) | The authentication value of a webhook is stored unencrypted in the organization database (never returned by reads). | Not scheduled |
| `AsynchronousStepPrimaryName` image (#87) | Asynchronous plug-in and webhook steps receive only the images registered on the step, not the automatic image Dataverse adds. | Not scheduled |
| Classic workflows | XAML is run by xrmverse's own interpreter: steps outside the designer's catalog fail explicitly; no workflow log, no Pause/Resume; business rules with Entity scope only. See [Classic workflows](workflows.md#known-limits). | Stays for the most part |
| Workflow studio | No concurrent edit detection (the last save wins); comments, key order and layout are kept only during the editing session, because the server stores XAML only; a process the YAML cannot represent opens read-only; no business process flows, dialogs or form-scope business rules. See [Workflow studio](workflow-studio.md#known-limits). | Not scheduled |
| Smart matching of incoming email | Incoming email is linked to earlier email by its `In-Reply-To` and `References` headers, the tracking token and the conversation index only; `correlationmethod` 6 (smart matching) is never set and the smart matching settings are not seeded. See [Correlation](email-and-queues.md#correlation). | Stays |
| Custom tables as email parties | The addresses of incoming email are matched against `account`, `contact`, `systemuser` and `queue` only; email-enabled custom tables are not matched. See [Parties](email-and-queues.md#parties). | Not scheduled |
| Inline images of incoming email | An image embedded in the HTML body by content id is imported as a plain attachment of the email; the body keeps its `cid:` reference, which is not rewritten, so the image is not shown in place. See [Email and queues](email-and-queues.md#known-limits). | Not scheduled |
| One process per database for the mailbox synchronization | The synchronization of incoming email assumes one `xrmd` process per organization database, as the job worker does. | Stays |
| Conversation index of sent email | Email sent by xrmverse, over SMTP or through Microsoft Graph, stores no conversation index, so the conversation index (method 5) links only replies to received email; replies to sent email are linked by their headers or the tracking token. | Not scheduled |
| A tracked message in a second queue | A message already imported gets no queue item in another queue mailbox that receives it, as with Dataverse's default; the setting that adds one is not available. See [Duplicates](email-and-queues.md#duplicates). | Not scheduled |
| Incoming mail without `Message-ID` | A message without `Message-ID` is identified by its `Date`, `From`, `To`, `Subject` and body length: a second message without `Message-ID` that has the same five values is not imported. See [Duplicates](email-and-queues.md#duplicates). | Stays |
| A message for a user and a queue | A message imported by a user's mailbox and later read by a queue's mailbox (both were recipients) is not imported again, so it reaches no queue. See [Duplicates](email-and-queues.md#duplicates). | Stays |
| Incoming side of an email server profile | `xrmd mailbox profile` and the web admin's profile dialog set the outgoing side of a profile only; the incoming columns of an IMAP profile are written with the Web API or the SDK. See [Setting up receiving](email-and-queues.md#setting-up-receiving). | Not scheduled |
| PreValidation steps of an incoming email delivery | The stage 10 steps of the messages of a delivery run inside the import's transaction, with the rest of the message, where a first-level request runs them outside. See [Plug-ins](plugins.md#incoming-email-deliveries). | Stays |
| `xrmverse_MailboxDeliveryId` does not exist in Dataverse | The shared variable that marks the plug-in contexts of an incoming email delivery is an xrmverse extension; a delivery guard that relies on it needs another rule on Dataverse. See [Building a delivery guard](email-and-queues.md#building-a-delivery-guard). | Stays |
| Email server profiles | Only SMTP with Basic authentication and Exchange Online through Microsoft Graph with an app-only token; other server types, protocols and credential sources (Exchange on-premises, IMAP/SMTP with OAuth, Outlook and Forward Mailbox delivery) are refused with a 400. See [Email and queues](email-and-queues.md#email-server-profiles). | Not scheduled |
| Email secrets (#86) | Profile and mailbox passwords and the Graph client secret are stored unencrypted in the organization database (never returned by reads), as the webhook authentication value is. | Not scheduled |
| Trigger on elastic tables | The *When a row is created, updated or deleted* trigger of the [cloud flow connector](flow-connector.md#the-trigger) does not support elastic tables; a subscription on one is refused. | Not scheduled |
| Flow callback URLs (#86) | The address of a flow subscribed through the [trigger](flow-connector.md#the-trigger) is stored apart from the subscription row but not encrypted, as webhook secrets are (issue #86). It is never readable through the Web API. | Not scheduled |
| Connector package tenant | The package that `xrmd connector export` writes names the tenant `common`, which works only for a multitenant client app; for a single-tenant app set the tenant ID on the connector's security step (see [Import the package](flow-connector.md#3-import-the-package)). | Planned |
| Trigger delivery through an outbound proxy | The server calls the flow directly; delivery does not go through an outbound HTTP proxy. | Not scheduled |
| Trigger subscriptions with several server instances | The subscription list is cached per server process: with several instances on one organization, a subscription change is seen by the others only after a restart. | Not scheduled |
| Sending as another user, delegate access | An email is sent from the caller's own mailbox or from a queue the caller can read; sending on behalf of another user and delegate access to a mailbox are not modelled. See [Email and queues](email-and-queues.md#known-limits). | Not scheduled |
| Email templates | `SendEmailFromTemplate`, `SendTemplate` and `InstantiateTemplate` are refused, and the `template` table is not seeded. See [Activities and notes](activities.md#what-is-refused). | Not scheduled |
| Queue form without members | The default queue form shows the queue's items but not its members: there is no seeded view of users to list them. Add and remove members with `xrmd queue member` or `AddPrincipalToQueue`. See [Email and queues](email-and-queues.md#default-forms-and-views). | To be fixed |
| Link from a system job to its record | `asyncoperation.regardingobjectid` is a plain id column, not a lookup: filter on it by value; it has no navigation property and no name. | Not scheduled |
| Queue privilege `prvPickFromQueue` | The message privilege is not modelled. Pick, Release, Route, Remove and the Update or Delete of a queue item need the queue item privilege, the item's queue readable (public, owned, a member, or shared) and Read on its record instead; no Write on the queue itself. See [Email and queues](email-and-queues.md#security-of-the-messages). | Stays until verified against Dataverse |
| No optimistic concurrency on queue items | Queue items have no ETag or `If-Match` check; the last writer wins on a plain Update. See [Email and queues](email-and-queues.md#known-limits). | Not scheduled |
| SharePoint document integration | Not available. | Planned (item 6.1, phase 6) |
| SDK-free servers (experimental) | An SDK-free server (`server init --sdk-free`) does not start yet: modules still use the Microsoft SDK internally. SOAP, Microsoft SDK plug-ins and migration from Dataverse are refused on such a server by design. See [SDK-free servers](microsoft-sdk.md#sdk-free-servers). | Planned (item S.2) |
| Context-level extension data of a parent read from an async payload | A plug-in of an async step that casts `ParentContext` to `RemoteExecutionContext` sees no `ExtensionData` on a parent level, even when the stored payload had unknown members on that level. Payloads this server writes never carry such members; only a row written by another program could. Extension data on values (entities, references and so on) is kept. | Not scheduled |
| In-process plug-ins without the Microsoft SDK | Plug-ins that run inside the transaction must be built against the Microsoft SDK; without it, only webhooks and workflows are available. | Planned (item S.4) |

### PostgreSQL organizations

Organizations on PostgreSQL have every feature of a SQL Server organization (item 2.7). The entries below are where they
still differ. See [PostgreSQL organizations](postgresql.md#known-limits).

| Limitation | Description | Status |
|---|---|---|
| `like` with character ranges on PostgreSQL | A `like` pattern with a range (`[a-c]`, `[^0-9]`) is refused with `'Like patterns with character ranges' is not available on PostgreSQL organizations yet.`: SQL Server orders a range by its collation, PostgreSQL by code points. Sets such as `[abc]` work. | Not scheduled |
| `like` with width, kana or `ß` on PostgreSQL | `like`, `begins-with` and `ends-with` ignore case but not width or kana type, and do not treat `ß` as `ss`: `like 'straße%'` does not find `STRASSE`, where SQL Server finds it. Equality (`eq`, `in`, joins, keys) ignores them on both engines. PostgreSQL 16 cannot run `like` on the collation that equality uses. | Not scheduled |
| Sorting of punctuation on PostgreSQL | Text sorts with case ignored and accents kept, as on SQL Server, but punctuation and symbols may sort in another order. | Stays |
| `Char` in formula columns on PostgreSQL | Power Fx `Char` gives an empty value for 0 and for surrogate codes (55296 to 57343), where SQL Server gives a character: PostgreSQL text cannot hold them. | Not scheduled |
| Long formulas on PostgreSQL | The SQL of a formula column is a few percent longer on PostgreSQL, so a formula close to the 300,000-character limit may be refused there and accepted on SQL Server. | Stays |
| Name length on PostgreSQL | Table, column and organization names longer than 63 bytes (UTF-8) are refused: PostgreSQL would cut them. | Stays |
| Date precision on PostgreSQL | Date and time values keep microseconds; SQL Server keeps 100-nanosecond units. | Stays |
| `distinct` on text columns on PostgreSQL | To show one stored value per group of equal values, a `distinct` query reads every matching row through a window function, so it costs more than on SQL Server on large results. The value shown for a group can be the spelling of another row with the same key (`ACME` for a row that stores `Acme`). | Not scheduled |
| Table locks on PostgreSQL | Removing solution components (uninstall, the cascade delete of a solution) locks the `solutioncomponent` table against other writers, and publishing or unpublishing duplicate detection rules locks `duplicaterule`, where SQL Server locks only ranges of rows. Readers are not blocked; concurrent writers wait, and two transactions that both write rules and publish them can deadlock (one fails and can be retried). | Not scheduled |
| Order of duplicates across engines | `RetrieveDuplicates` and the duplicate check order candidates by GUID, which SQL Server and PostgreSQL sort differently: with more matches than the page, the two engines can return different subsets. | Stays |
| Elastic table JSON on PostgreSQL | A JSON object with a key twice keeps the last value (SQL Server sees both); a value with `\u0000` or a lone surrogate escape is treated as not JSON; a range condition on a JSON string compares UTF-8 bytes where SQL Server compares UTF-16 code units, so the order differs between characters above U+FFFF and those from U+E000 to U+FFFF. | Stays |
| Role mapping in a migration when several roles match | `xrmd migrate` maps a role the organization lacks by its template, else by its name (case ignored); when several roles of the organization match (the same role in several business units), it takes the first by GUID, which is not the same role on SQL Server and PostgreSQL. | To be fixed |
| One database engine per server | Every organization of a server uses the same engine, chosen at `xrmd server init`; an organization cannot move from one engine to the other. | Stays |

### Model-driven apps

| Limitation | Description | Status |
|---|---|---|
| Rendering | The model-driven UI is xrmverse's own renderer: faithful for the supported Client API, not pixel-identical to Microsoft's Unified Interface. Two looks are available, classic and modern (see [Looks: classic and modern](model-driven-apps.md#looks-classic-and-modern)). | Stays |
| Solutions using `XrmVerse` or `xrmverse_AppOnLoad` do not import into Dataverse | The `xrmverse_AppOnLoad` setting definition does not exist in Dataverse and scripts that call `XrmVerse` fail there. Keep such apps out of solutions that you also deploy to Dataverse. See [xrmverse extensions](model-driven-apps.md#xrmverse-extensions). | Stays |
| Subgrid filtered by a form column is xrmverse-only | A subgrid set to list the records matching a lookup of the form keeps a standard FormXml with no relationship; the filter is in the JSON web resources named by the `xrmverse_SubgridFilters` setting, which Dataverse does not have. In Dataverse the same subgrid lists all records of its view. See [Subgrids filtered by a form column](model-driven-apps.md#subgrids-filtered-by-a-form-column). | Stays |
| One `xrmverse_SubgridFilters` value per app | An app has one `appsetting` row per setting: two solutions that set `xrmverse_SubgridFilters` for the same app overwrite each other's value, and the last import wins. The environment's value is merged with the app's. The form designer stores its entries only in the first resource named by the environment's value. See [Known limits of the subgrids filtered by a form column](model-driven-apps.md#known-limits-of-the-subgrids-filtered-by-a-form-column-x11). | Stays |
| An unreadable subgrid filter resource hides dataset controls | While a web resource named by `xrmverse_SubgridFilters` cannot be read, every subgrid of the app without a relationship that is replaced by a dataset PCF control shows a notice instead of the control, since xrmverse cannot tell which subgrids the missing resource filters. See [Known limits of the subgrids filtered by a form column](model-driven-apps.md#known-limits-of-the-subgrids-filtered-by-a-form-column-x11). | Stays |
| Choice text colour | The text colour of a coloured choice value is an xrmverse extension, kept in the `choices` key of the modern look's presentation overlay; Dataverse ignores it. The background colour is the option's own `Color` and round-trips with Dataverse. See [Choice colours](model-driven-apps.md#choice-colours-modern-look). | Stays |
| Attachment preview | The email form previews PDF files, images and plain text only; HTML, SVG and XML attachments are never rendered and can only be downloaded. See [Email form](model-driven-apps.md#email-form). | Stays |
| HTML body of a received email (#335) | The email form shows the HTML body of a received email as source text instead of rendering it. | Planned (#335) |
| `xrmverse_EmailSendFrom` does not exist in Dataverse | The sender policy that allows sending from queues only is an xrmverse setting; Dataverse has no such switch. See [Sender policy](email-and-queues.md#sender-policy-xrmverse_emailsendfrom). | Stays |
| Side panes show web resources only | `navigate` in a pane accepts only `pageType: "webresource"`; a form or a list in a pane is rejected. Panes are not restored after a browser reload (the OnLoad handlers create them again). See [xrmverse extensions](model-driven-apps.md#known-limits-of-the-xrmverse-extensions-x10). | Not scheduled |
| More from the form layouts | In the modern look the wizard is for new records only; there are no step-change events for scripts, no grid placement of the header, the footer or single fields, no timeline options per control, no background colours for tabs, the header, the footer or subgrids, quick views and web resources, and no changing a colour from a script. A free `#rrggbb` background colour is the same in the light and dark theme (only the named colours follow the theme). The layouts are edited in the web admin's form designer (see [Forms](web-admin.md#forms)). See [Form layouts](model-driven-apps.md#form-layouts-modern-look). | Not scheduled |
| Field colour on fields without a box | A field colour of the modern look fills the field's control box. Multiple-choice (check box group), file and image fields have no such box, so they are not coloured; a section colour still shows behind them. See [Background colours](model-driven-apps.md#background-colours-background). | Not scheduled |
| More from the view modes | In the board and the calendar of the modern look there is no creating a record from a board column or a calendar day, no drag on the calendar, no week or day calendar, no colour of the calendar's bars by a choice column, no swimlanes and no totals per board column. Subgrids on a main form can be a board, a calendar or cards (see [Subgrids](model-driven-apps.md#subgrids)); associated views (related tabs), dashboards and subgrids inside quick view forms stay grids. Wrapping text is set per view and column in the modern look only (the classic look keeps every cell on one line), not per subgrid, and a wrapped column's width is not adjusted to its text. See [View modes](model-driven-apps.md#view-modes-modern-look). | Not scheduled |
| More from the form designer | The web admin's form designer does not create or configure timelines, spacers or iframes (it keeps the existing ones as they are), and does not edit business rules, the footer, related tabs (navigation), display conditions or roles, the form order, the form's presentation or the header's density; it does not edit tab events (`TabStateChange`) or control events other than OnChange (it keeps them as they are); fields are added with the field picker, not dragged from a field list onto the canvas; it has no live data preview, does not edit card, dashboard or other form types, and has no quick create at run time in the model-driven apps. See [Forms](web-admin.md#forms). | Not scheduled |
| Cross-tab drag | In the form designer a field or a section is dragged only within the tab shown, and a field is not dragged between the body and the header; the move buttons take it to another tab. See [Forms](web-admin.md#forms). | Not scheduled |
| 250 records on the board and the calendar | The board and the calendar read at most 250 records in one request, with no paging, and say so when the view has more; narrow the view or use quick find. | Stays |
| External looks (renderer packages) | Only the built-in classic and modern looks exist; a look delivered as a bundle in a web resource is not loaded. An app or user that names another look gets the classic look with a notice. | Not scheduled |
| Client API namespaces (#132) | `Xrm.Panel`, `Xrm.App`, `Xrm.Device` and `Xrm.Encoding` are not available. | Not scheduled |
| Modern commands (#184) | Commands built with the modern command designer (`appaction`) are not shown on forms; classic ribbon commands work. | Not scheduled |
| Timeline posts (#213–#218) | No auto posts, likes, follows, attachments on posts, `#` record references, personal wall, mention notifications or rich text posts; the timeline's form designer settings are not read. See [Model-driven apps](model-driven-apps.md#known-limits-of-the-timeline-ui-1g). | Not scheduled |
| More from the view designer | The web admin's view designer does not edit personal views, related columns more than one lookup away or over one-to-many and many-to-many relationships, conditions on related tables, aggregate views, or the raw FetchXML and LayoutXML; it does not create a presentation overlay (choose one on the Environment page or per app), and there is no view designer inside the model-driven apps and no chart designer. See [Views](web-admin.md#views). | Not scheduled |
| Leaving the view designer or the form designer with unsaved changes | Each designer's **Back** button asks before dropping unsaved changes; the browser's Back button, the address bar and links elsewhere on the page leave without asking. | Stays |
| Environment theme on the apps page | The apps page (`/apps`) and the workflow studio use the xrmverse theme in the user's light or dark colour scheme; the app tiles' banner follows that theme's brand colours. The environment's presentation overlay theme (brand colours, font, density), which the web admin applies for a System Administrator and a modern app applies inside the app, is not applied there. See [Model-driven apps](model-driven-apps.md#what-the-app-shell-offers). | Not scheduled |
| One row at a time in the web admin lists | The lists of the web admin select one row at a time: a command (disable a user, delete a solution, cancel a job) acts on that row. There is no multi-select and no bulk command. See [Web admin](web-admin.md#look-and-feel). | Not scheduled |

### Migration from Dataverse

| Limitation | Description | Status |
|---|---|---|
| Email data in a migration | Email attachments, mailboxes and email server profiles are not copied by `xrmd migrate`. See [Migrating from Dataverse](migration.md). | Not scheduled |
| Mapped tables: merging two source tables into one target | A [map](migration.md#mapping-tables-to-a-solutions-tables) sends one source table to one target table; two source tables cannot go to the same target, and a run whose maps would send two tables of the package to one table is refused at the start. | Not scheduled |
| Mapped tables: values computed beyond one lookup | A derived value reads one column through one lookup of the source row, with a value map; longer paths, calculations and text transformations are not available. | Not scheduled |
| Mapped tables: app modules, site maps and role privileges | Entries that reference a removed table are skipped with a warning; site maps and system dashboards are stored as they are. | Not scheduled |
| Mapped tables: Microsoft tables outside the exclusion catalog | A Microsoft table whose publisher prefix is not in the exclusion catalog (for example `msfp_`, `mspcat_`) is treated as a customer table and is never removed. | Not scheduled |
| Mapped tables: value matching by label | `byLabel` compares the source labels with the target's English (1033) labels; xrmverse has no organization base-language setting. | Not scheduled |
| Mapped tables: `customColumns: drop` issue column | Under `customColumns: drop` the issue names the source column, not a target column. | Not scheduled |
| Archive tables: column types | Image, file and unique-identifier columns are not created (counted as `column-not-mapped`); string and number formats that are not listed fall back to the default. | Not scheduled |

## Differences that stay

Choices that make xrmverse work outside Microsoft's cloud. They will not change.

| Difference | Description | Status |
|---|---|---|
| Where it runs | One `xrmd` process per organization on your servers or your cloud; scalability, high availability, updates and support depend on whoever runs it. See [Multiple organizations](multiple-organizations.md). | Stays |
| Database | SQL Server with direct access (Express edition by default), or PostgreSQL 16 or later with the limitations above. See [SQL Server editions](sql-server-editions.md) and [PostgreSQL organizations](postgresql.md). | Stays |
| Identity | Microsoft Entra ID or xrmverse's own identity provider. See [Local identity provider](local-idp.md). | Stays |
| Files | File system, S3-compatible storage or Azure Blob Storage instead of Dataverse's managed storage. See [Files and images](files-and-images.md#known-differences-from-dataverse). | Stays |
| Elastic tables | Emulated on SQL Server or PostgreSQL instead of Cosmos DB, with a basic subset of JSON queries. See [Elastic tables](elastic-tables.md). | Stays |
| Custom APIs | Created over the SDK, SOAP, the Web API or `xrmd customapi create`; a managed custom API is never changed in place. See [Custom APIs](custom-apis.md#known-limits). | Stays |
| Migration from Dataverse | `xrmd migrate` maps roles onto the organization's roles by id, template or name; an assignment to a role the organization lacks is reported, not created. Forms, views, charts, roles and processes made outside the exported solutions are not exported. See [Migrating from Dataverse](migration.md). | Stays |
| Mapped tables: managed solutions with components of a mapped table | A managed solution of the package that has components of a mapped or missing Microsoft table, or lookups that would be retargeted, fails the run with the list of removals and retargets; a managed solution is never rewritten. See [Migrating from Dataverse](migration.md#mapping-tables-to-a-solutions-tables). | Stays |
| Mapped tables: processes and plug-in steps | Processes and plug-in steps whose primary table is a mapped or missing Microsoft table are removed from customer solutions and listed (`component-of-mapped-table`, `component-of-missing-table`); re-create them on the solution's tables. | Stays |
| Mapped tables: options added to a choice column | Options added to a carried choice column that uses a global choice appear on every column that uses that choice; a value already present with another label is kept as it is. | Stays |
| Archive tables: status reasons | The source's status reasons are kept as copy columns (`xrmva_statecode`, `xrmva_statuscode`), not as the table's own status model; only an activity archive follows the source state. | Stays |
| Error retries | After a SQL deadlock or timeout the server never retries by itself (pre-validation plug-ins would run twice): the caller retries, with the same error codes as Dataverse. | Stays |
| Related records open in a side pane | In a model-driven app a lookup's value and a form subgrid's rows and **New** open the related form in a side pane two thirds as wide as the form, where Dataverse opens the full page. Set the app setting `xrmverse_LookupOpenMode` to `page` for Dataverse's behaviour, or use Ctrl+click. See [Opening related records](model-driven-apps.md#opening-related-records). | Stays |
| Share on several records | The view command bar's **Share** on several selected records only grants rights (`GrantAccess`) and has no current-shares tab; the commands run one request per record and the view does not check each record's rights first, so a refused record is listed in the result. See [Model-driven apps](model-driven-apps.md#known-limits). | Stays |
| Share pane | The form's **Share** command opens a side pane with two tabs (current shares with one column per right, and users or teams to add) instead of Dataverse's dialog. It sends the same messages (`GrantAccess`, `ModifyAccess`, `RevokeAccess`). See [Model-driven apps](model-driven-apps.md). | Stays |
| Tracking tokens off by default | A new organization has no tracking prefix, so `SendEmail` adds no tracking token to the subject; a new Dataverse environment has them on (from memory, not yet verified). Set `organization.trackingprefix` to turn them on. See [Tracking tokens](email-and-queues.md#tracking-tokens). | Stays |
| A mailbox that cannot send fails the email | When the sender's mailbox is not approved, has no profile or has not passed its test, the email is set to Failed (state 0, status 8) with the reason on the send job; from memory (not yet verified), Dataverse leaves it Pending Send until the mailbox works. | Stays |
| Graph send retried after a timeout | When a send through Microsoft Graph times out after Graph accepted the message, the retry can deliver it twice. | Stays |
| Tracking token base | `organization.trackingtokenidbase` is not supported: a value other than empty or 0 is refused with a 400 that names the column. Tokens are the tracking prefix followed by the counter. See [Tracking tokens](email-and-queues.md#tracking-tokens). | Not scheduled |
| Columns written by the send job not audited | `email.messageid` and `email.deliveryattempts`, written by the send job, are not in the email's audit history; the change of state is. | Stays |
| Object type code of `organizationsetting` | The table that holds the environment values of settings has object type code 9967, assigned by xrmverse. Code that relies on the number Dataverse uses may not find it; use the logical name. See [Tables in every organization](system-tables.md). | Stays |

## Not in xrmverse

Dataverse features and neighbouring products that xrmverse does not offer and has no plan for.

| Feature | Description | Status |
|---|---|---|
| Power Automate | The Dataverse connector is not available. xrmverse has its own [cloud flow connector](flow-connector.md) with row actions, custom APIs and the trigger *When a row is created, updated or deleted* (on the rows of one table). | Stays |
| Local identity provider organizations and the connector | The [cloud flow connector](flow-connector.md) signs in through Microsoft Entra ID; organizations in Local identity provider mode are not supported, and `xrmd connector export` refuses them. | Planned |
| On-premises data gateway | Not supported by the [cloud flow connector](flow-connector.md): the organization must be reachable over public https. A gateway cannot carry the server's calls back to a flow. | Not scheduled |
| Certified connector | The [cloud flow connector](flow-connector.md) is imported by the customer, once per organization; it is not published in the public connector gallery. | Not scheduled |
| FetchXML paging in the connector | The result of a FetchXML query in the [cloud flow connector](flow-connector.md) is not paged; file, image and party list columns are not offered for writing. | Not scheduled |
| Canvas apps and custom pages | Not available; model-driven apps are. | Stays |
| Business Process Flows | Not available. | Stays |
| Editable grids | Not available. | Stays |
| Offline mobile app | Not available. | Stays |
| Copilot and AI Builder | Not available; AI agents use the built-in [MCP server](mcp.md). | Stays |
| Dataverse search | Relevance search, vector search and semantic models are not available; quick find works. The glossary and synonym tables are kept for agents (see [Glossary and synonyms](glossary-and-synonyms.md)). | Stays |
| Virtual tables | Not available. | Stays |
| Power Pages | Not available and not planned. | Stays |
| Position hierarchy | Only the manager hierarchy is supported. See [Security](security.md#hierarchy-security). | Stays |
| Several organizations in one process | Each organization runs its own `xrmd` process. | Stays |

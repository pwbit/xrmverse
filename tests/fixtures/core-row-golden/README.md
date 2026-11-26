# Core row golden dumps (S.2e3b)

Each `{scenario}.txt` is what the CRUD handlers of the core stored and answered for one scenario in
`tests/Xrmd.Integration.Tests/CoreRowGoldenTests.cs`, captured on the code before they move onto the model types (plan S.2e3b,
Task 1; Review Focus 1 and 2). Later tasks keep these files byte-identical.

## Write scenarios
A write scenario arranges its rows (with the administrator's `OrganizationService`), then runs its request once through
`PipelineExecutor.ExecuteAsync` as the administrator at depth 1, with no session (the pipeline opens one). `CreateInMigrationScope` is
the exception: inside a `MigrationScope` the pipeline refuses to run, so its Create goes to `CoreOperations.CreateAsync` in a committed
transaction of its own, as the migration import writes. A file holds:

- `--- request ---`: the request as it was sent (the `CoreDump` writers of S.2e3a);
- `--- response ---`: the response CLR type, `ResponseName` and results, or the fault that replaced it (exception type, error code and
  HTTP status of an `XrmdFaultException`, message);
- `--- rows ---`: the rows the call touched, read afterwards with plain SQL (`SELECT *` on the row's key): one block per table and key
  with the number of rows, then each row with every column in name order (lower case), values as `Type:value` (`null` for a NULL).
  The tables are the data rows, `activitypointer` and `activityparty` of an activity, `audit` of an audited record (`changedata`
  verbatim), the record's row in the match code table of the published rule (`meta.matchcode_{rule}`), the mail secrets
  (`meta.mail_secret`), the `fileattachment` rows of a note body, an attachment body or an image with the bytes of each blob in the
  file store, the intersect rows of an N:N relationship and `systemuserroles`. After a Delete, the file store is asked for each
  blob the deleted rows had (`file store blob {a:...-file}: none`), once the host's deletion queue is drained. A fault scenario writes the number of rows
  its request would have written (0), or the row it would have changed.

Rows of one block are ordered by their text with the ids named so far written by name and every other id as `?`, so the order does
not depend on the ids the database gave them (`meta.mail_secret` is ordered by `name`).

### Columns left out
On a table other tests share (anything but the `new_crr*` tables of these goldens and the `new_crg*` tables of S.2e3a, which only the
goldens use) the columns of custom attributes are left out, with their companion columns (`_base`, `type`...): another test of the run
may have added one, and the file must not depend on which tests ran first. The system columns of the seeded tables are all written.

## Read scenarios
A read scenario (`Read*.txt`) arranges a record, then runs its request (a): through the pipeline as above, and (b): its query in the
model form (`CoreOperations.ReadQuery` of the same query, or the query `CoreOperations.RetrieveAsync` builds for a Retrieve: the
columns, the primary key condition and `Top = 1`) through `QueryExecutor.ExecuteAsync` directly, as the administrator. Both answers are
written with the `CoreDump` writers: an entity's `Attributes` and `FormattedValues` in collection order (no read selects all columns,
so the order is the query's, whatever the state of the metadata cache).

## Normalisation
As in S.2e3a (`tests/fixtures/core-response-golden/README.md`), with these differences:
- Named ids are `{a:label}`: the rows a scenario arranges (`{a:account}`, `{a:contact}`, `{a:email}`, `{a:rule}`...) and the row a
  Create made (`{a:created}`, the id its response returned; the rows are read by that id, so a wrong id shows as a missing row).
  Every other id is `{idN}` in first-seen order over the file.
- A date before 2024 is a fixed value a scenario wrote (`overriddencreatedon`, a migrated `createdon`, the `new_when` and `new_day`
  columns) and is written as it is, in a response, a row (`yyyy-MM-ddTHH:mm:ss.fffffff`, no kind) and a formatted value. Any later date
  is the time of a call: `{date:Kind}` in a response, `DateTime:{date}` in a row, `{date}` in a formatted value.
- Row versions (`versionnumber`) are `{rowversion}`. In a row, an object type code of 10000 or more (a custom table's, in a column
  ending in `type` or `typecode`) is `{otc}`; the match code table, whose name holds the rule's id, is written `meta.matchcode_{rule}`. Columns whose value changes with every call although it is no id or date
  (`new_pic_timestamp`) are `{volatile}` when not null.
- A `byte[]` longer than 64 bytes is its length and SHA-256: the image the image scenario sends, which is a fixed byte constant in
  the test (`RowKit.ImageBytes`), not the output of an encoder.
- The stored image thumbnail (`new_pic`) is written as its decoded format, size and the colours of four pixels well inside its quarters
  (as `ThumbnailerTests` reads it), not as bytes: the server resamples it with Skia, whose raster path differs with the CPU (SSE, AVX2,
  AVX-512), so the bytes near the colour edge may differ between machines.

## Determinism
The scenarios use fixed names (`crr ...` rows; the tables `new_crrtypes`, `new_crrtag` and the N:N `new_crrtypes_new_crrtag`,
`new_crrimage`, `new_crrkey`, created once; S.2e3a's `new_crgelastic` and `new_crgaudited`). A read deletes its earlier rows first; a
fault scenario that counts rows deletes those an earlier run left. `CreateAllTypesAutoNumber` sets the seed of `new_code` to 100
before its Create, `CreateMoney` to `AutoNumberFormat.DefaultSeed` (1000). What a scenario turns on is turned off again when the kit
is disposed: the organization's auditing, duplicate detection on contact (with the rule it published), the administrator's time zone
and date format (`ReadUserDateFormat`).

## Providers
The files are the same on SQL Server and PostgreSQL: a local run with `XRMD_TEST_PROVIDER=postgresql` (Testcontainers,
`postgres:16-alpine`) passed against the files written on SQL Server, so there is one set.

The parameters `EntityRowMapper.ToParameters` builds are pinned without a database by
`tests/Xrmd.Core.Tests/EntityRowMapperGoldenTests.cs`, one file per provider dialect in `tests/fixtures/row-parameters-golden`
(`sqlserver.txt`, `postgresql.txt`): a row with a value of every column type and a row of nulls, each parameter as the mapper built it
(name, `DbType`, size, precision, scale, value), then as the provider's own parameter after `DbCommandExtensions.AddParameters` with the
dialect's adjustments (`SqlDbType`; `NpgsqlDbType` and data type name).

## Provenance
Captured from the code at base commit 94de2032 (plan S.2e3b Task 1). The write-if-missing rule is the same as for the S.2e3a goldens:
a missing file is written once and the test fails, and an existing file is never rewritten. `.gitattributes` turns off line-ending
conversion for this directory.

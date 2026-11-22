# Autonumber columns

[← xrmverse documentation](../README.md#documentation)

## Introduction

An autonumber column fills itself with a generated value, such as `INV-01042` or `CASE-2026-0007-K3PZ`, when a record is
created. You give a Text column an `AutoNumberFormat`: a pattern of fixed text and placeholders.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Placeholders | `{SEQNUM:n}`, `{RANDSTRING:n}`, `{DATETIMEUTC:format}` | Same |
| When the format is checked | When a record is created, not when the column is defined | Same |
| More than one `SEQNUM` | Refused with 400 | **Differs**: refused rather than guessed |
| Gaps in the numbering | Possible (a failed Create does not give its number back) | Same |
| Seed messages | `SetAutoNumberSeed`, `GetAutoNumberSeed`, `GetNextAutoNumberValue` | Same; the seed is not part of a solution, as in Dataverse |
| Storage | One SQL Server `SEQUENCE` per column | xrmverse's own implementation |

## Defining an autonumber column

The column must be a string column with format **Text**; a format on any other column is a 400. You can set the format with:

| Tool | How |
|---|---|
| SDK | `CreateAttribute`, `UpdateAttribute`, or the attributes of `CreateEntity` (including the primary name) |
| Web API | `POST EntityDefinitions(...)/Attributes` |
| Schema YAML (`xrmd apply`) | `autoNumberFormat` on a column or on `primaryName` (see [CLI xrmd](cli.md)) |
| Solutions | Imported and exported with the column (see [Solutions](solutions.md)) |

### Format placeholders

| Placeholder | Produces |
|---|---|
| `{SEQNUM:n}` | A sequence number, zero padded to at least n digits; it keeps growing past n digits |
| `{RANDSTRING:n}` | n upper-case letters and digits, n from 1 to 6 |
| `{DATETIMEUTC:format}` | The UTC time in a .NET date and time format |
| Any other text | Copied as is |

## How values are generated

- On **Create**, each autonumber column that the request leaves without a value (absent, null or empty) gets one. A value
  the caller gives is kept.
- **Update** never generates a value.
- Rows that already exist when a column becomes autonumber are not filled.

### Errors

The placeholders are not checked when the column is created or changed. The **Create of a record** fails with 400 when:

- the format is not valid: an unknown placeholder, `RANDSTRING` outside 1..6, `SEQNUM` below 1, a date format .NET refuses,
  or more than one `SEQNUM`;
- the generated value is longer than the column's `MaxLength` (it is never truncated).

## The sequence and its seed

Each column has a SQL Server `SEQUENCE` (`[autonumber].[<column metadata id>]`) that starts at **1000**.

| Event | Effect on the sequence |
|---|---|
| A Create takes a number and then fails | The number is not given back: the numbering can have gaps |
| The format changes | The sequence is kept |
| The format is emptied | The sequence is removed |
| The column or its table is deleted | The sequence is dropped |

### Seed messages

| Message | Output | Privilege | Web API |
|---|---|---|---|
| `SetAutoNumberSeed` (`EntityName`, `AttributeName`, `Value`) | Restarts the sequence at `Value` | `prvWriteAttribute` | `POST SetAutoNumberSeed` (204) |
| `GetAutoNumberSeed` | `AutoNumberSeedValue`: the last seed set, 1000 by default | Any organization member | `GET GetAutoNumberSeed(EntityName='…',AttributeName='…')` |
| `GetNextAutoNumberValue` | `NextAutoNumberValue` | Any organization member | `GET GetNextAutoNumberValue(…)` |

The seed is not part of a solution: set it again in each organization.

## See also

- [Solutions](solutions.md)
- [Known issues and limitations](known-issues.md)

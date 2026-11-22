# Currencies and exchange rates

[← xrmverse documentation](../README.md#documentation)

## Introduction

Every organization has a **base currency** and a list of other currencies, each with an exchange rate to the base. A record
that holds money keeps its own currency and the rate it was saved with, and the server stores a second value for each money
column, converted to the base currency, so that totals in the base currency can be compared. xrmverse follows the Dataverse
model of tables, columns, messages and error codes. Whether each detail matches a real Dataverse is not verified. The Microsoft documentation describes the feature in
[Transaction currency table](https://learn.microsoft.com/power-apps/developer/data-platform/transaction-currency-currency-entity),
[Manage transactions with multiple currencies](https://learn.microsoft.com/power-platform/admin/manage-transactions-with-multiple-currencies)
and the [table references](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/transactioncurrency)
of `transactioncurrency`, `organization` and `usersettings`. This page says what xrmverse does and where it stops.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Model | Base currency, `transactioncurrency` rows, `exchangerate` and `_base` per money column | Same tables, columns, messages and error codes |
| Name and symbol of a System currency | Required: xrmverse has no catalog | **Narrower**: Dataverse fills them in |
| Precision | 0 to 4 | **Narrower**: Dataverse allows more in the column |
| Formatted money | en-US grouping and the organization's format | **Narrower**: per-user number formats are not modelled |
| Rate changes | Do not recompute records until they are saved again | Same |
| Solutions | Currencies are data, never carried | Same |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Choose the base currency | `Seed:BaseCurrency` before the first start | [The base currency](#the-base-currency) |
| Add a currency or change a rate | `xrmd currency`, web admin **Currencies**, SDK or Web API | [Adding currencies and rates](#adding-currencies-and-rates) |
| Read a rate | `RetrieveExchangeRate` | [RetrieveExchangeRate](#retrieveexchangerate) |
| Understand stored values | `exchangerate`, `_base`, rounding | [How `exchangerate` and `_base` are computed](#how-exchangerate-and-_base-are-computed) |

## The base currency

A new organization is seeded with one base currency: euro (`EUR`, symbol `€`, 2 decimals, rate 1) unless you say otherwise.
`organization.basecurrencyid` points at it, and `basecurrencysymbol` and `basecurrencyprecision` hold copies of its symbol and
precision (read-only; the server refreshes them when the base row is edited). Set the base currency before the first start of
an organization with these keys (see [Configuration](configuration.md)):

| `.env` variable | Key | Meaning |
|---|---|---|
| `SEED_BASE_CURRENCY` | `Seed:BaseCurrency` | ISO 4217 code; empty or not set means not given, and a new organization then gets `EUR`; an unknown code stops startup with an error that names the key |
| (not in `.env`) | `Seed:BaseCurrencyName` | name, up to 100 characters; default the code (`Euro` for `EUR`) |
| (not in `.env`) | `Seed:BaseCurrencySymbol` | symbol, up to 5 characters; default the code (`€` for `EUR`) |
| (not in `.env`) | `Seed:BaseCurrencyPrecision` | decimal places 0 to 4, default 2 |

- **It is fixed once set.** An organization that already has a base currency keeps it. If `Seed:BaseCurrency` names another
  code, the server logs a warning and changes nothing. An organization made before this feature gets its base currency at the
  next start.
- **The base row is protected.** Its rate stays 1 (any other rate is `0x80048cf5`), it cannot be deactivated (`0x80048cf4`) or
  deleted (`0x80048cff`), and its code and type cannot change. Its name, symbol (up to 5 characters, never cut) and precision
  can. `organization.basecurrencyid` cannot be updated (400).
- The organization also holds `pricingdecimalprecision` (0 to 4, default 2), `currencydisplayoption` (0 symbol, 1 ISO code)
  and `currencyformatcode` (0 `$123`, 1 `123$`, 2 `$ 123`, 3 `123 $`); see [FormattedValue](#formattedvalue-of-money).
- `xrmd server org add` creates organizations with the euro as base currency; `Seed:BaseCurrency` applies to the main
  organization the first time it is created.

## Adding currencies and rates

A currency is a row of `transactioncurrency` (entity set `transactioncurrencies`, object type code 9105): `currencyname`,
`isocurrencycode`, `currencysymbol`, `currencyprecision` (0 to 4), `exchangerate` and `currencytype` (0 System, 1 Custom), plus
`statecode` 0 Active / 1 Inactive. Four ways to manage them:

- **CLI:** `xrmd currency list|create|rate|activate|deactivate|pricing-precision` (see [CLI](cli.md#currencies)).
- **Web admin:** the **Currencies** page (`/manage/currencies`), System Administrators only (see [Web admin](web-admin.md#currencies)).
- **SDK and Web API:** create and update the rows like any other table. `RetrieveExchangeRate` reads a rate (below).
- **Seed:** only the base currency.

Rules, all checked by the server:

- Name and symbol are required. xrmverse has no built-in catalog of names and symbols, so it does not fill them in for a
  System currency.
- A **System** currency needs a code from the ISO 4217 list embedded in xrmverse; a **Custom** one needs exactly three capital
  letters. Anything else is `0x80048cf2`. A code that exists already is `0x80048cf3`.
- The rate is required and must lie between 0.0000000001 and 100000000000 (`0x80048cfd` otherwise, checked before the
  generic range error). The rate is stored with up to 12 decimals, a record keeps 10.
- A precision above 4 is a 400 (see [Precision and rounding](#precision-and-rounding)).
- A currency that records still use cannot be deleted (the relationship is Restrict), and neither can one that a `usersettings`
  row names. Deactivate it instead: an inactive currency cannot be given to a record (`0x80048cfc`), while records that
  already have it can still be updated.
- Creating, updating and deleting currencies needs the privileges of the System Administrator role. The Basic User role can
  read currencies and use them on records (Read and AppendTo, Global).
- These rules (and those on `organization` and `usersettings`) are checked again on the values the server writes, in the
  transaction of the write and after the pre-operation plug-ins: a plug-in that changes the base currency's rate or state, or
  sets a precision of 7, gets the same error as a client.

The caller's own default currency is a column of **`usersettings`** (entity set `usersettingscollection`, object type code 150,
user-owned, key `systemuserid`). Every user has one row: the seed, the startup and the creation of a `systemuser` write it.
Only `transactioncurrencyid` is modelled. Basic User can read and write its own row and append to it.

## Which tables get the columns

A table gets `transactioncurrencyid` (lookup to `transactioncurrency`), `exchangerate` (Decimal, 10 decimals) and the
relationship `TransactionCurrency_<table>` (Delete Restrict) when it gets its first money column. This holds for every way a
column appears: `CreateEntity`, `CreateAttribute`, a solution import, `xrmd apply`. Each money column also has a
`<name>_base` column (Money, 4 decimals, ±922337203685477), made by the server.

- The pair stays when the last money column is deleted. It is a system attribute: deleting it is 403 `PrivilegeDenied`, and
  deleting a `_base` is `0x80048cfe`. Nobody can define a column named `transactioncurrencyid` or `exchangerate`.
- `account` and `contact` have the pair from the seed, although xrmverse seeds no money column on them.
- On the Web API the lookup is `_transactioncurrencyid_value`, its navigation property is `transactioncurrencyid` on every
  table (`transactioncurrencyid@odata.bind`, `$expand=transactioncurrencyid`), as Dataverse answers for a custom table
  (checked on `op365_project`, #183). Organizations made before this fix had `TransactionCurrencyId` on custom tables: the
  next start renames it.

## How `exchangerate` and `_base` are computed

The server does it in the transaction of the `Create` or `Update`, at stage 30 (after the pre-operation plug-ins, next to
`fullname`). Pre-operation plug-ins do not see `exchangerate` or the `_base` values in `Target`; post-operation images do.

- **Currency on Create.** The record's `transactioncurrencyid` if it has one, else the `usersettings` currency of the
  effective caller (the impersonated user, not the service account) if it is set and active, else the base currency. A
  `transactioncurrencyid` sent as null counts as not given. This runs on every table with the pair, also when the record has
  no money. A currency that does not exist is 404; an inactive one is `0x80048cfc`.
- **Rate.** `exchangerate` is the currency's rate rounded to 10 decimals. `_base = Round(value / exchangerate, 4)` with that
  stored rate, so anyone can reproduce it from the row. A null money value gives a null `_base`.
- **Update.** The server refreshes the rate and recomputes **every** `_base` of the row, from the stored values of the money
  columns the request does not carry, when the Update sets `transactioncurrencyid` or a money column. An Update that touches
  neither leaves rate and `_base` as they are. Setting the currency to null while any money value is not null is
  `0x80048cfb`; when all money is null it clears `exchangerate` too.
- **Values you send are ignored.** `exchangerate` and `_base` in a request are dropped, as every read-only column is. A
  `_base` set by a pre-operation plug-in is overwritten.
- **Rate changes do not recompute.** Changing the rate of a currency touches no record: each record keeps its rate until it is
  saved with a money or currency change. No change-tracking or audit entry is written for those records.
- **Overflow.** A `_base` beyond ±922337203685477 is `0x80048cec` (above) or `0x80048ceb` (below), naming the column.
- **Audit and change tracking.** `exchangerate` and `transactioncurrencyid` are audited like `fullname`, when the value changed
  and the column is audited. `_base` columns are never audited.
- A rate that is null or rounds to 0 on a currency a write uses is `0x80048cfd`.

Example: `xv_budget` 100 in USD at 1.10 is stored as 100.00, `exchangerate` 1.1 and `xv_budget_base` 90.9091.

## Precision and rounding

The decimals of a money column depend on its `PrecisionSource`: 0 uses the column's own `Precision` (2 if none), 1 uses
`organization.pricingdecimalprecision`, 2 (the default) or none uses the `currencyprecision` of the record's currency.
Every money value in a request is rounded to that, half away from zero (`2.345` to `2.35`, `-2.345` to `-2.35`). `_base`
values are rounded to 4 decimals, the scale of the column; xrmverse does not round them to the base currency's precision.

- Precision of currencies and of `pricingdecimalprecision` is 0 to 4. A larger value is a 400, because money columns are
  `decimal(19,4)`. Dataverse allows more in the column itself; widening every column is a migration that xrmverse does not do.
- Money columns accept up to ±922337203685477 in practice: a value that gives a larger `_base` at rate 1 is `0x80048cec`.
- Stored money is not rounded again when you change a record's currency to one with fewer decimals; only values in the
  request are rounded.

## FormattedValue of money

Reading money with `IncludeFormattedValues` (SDK) or `Prefer: odata.include-annotations` (Web API) gives the amount with the
effective precision, en-US digit grouping, and the symbol placed by `currencyformatcode`: `€123.00`, `123.00€`, `€ 123.00`,
`123.00 €`. With `currencydisplayoption` 1 the ISO code replaces the symbol. A negative amount puts `-` first
(`-€123.00`). The currency is the record's own; `_base` uses the base currency's symbol and precision. For a column read through a
`link-entity`, the linked row's currency is used. A row with no currency keeps the old format (two decimals, no symbol).
Changing a rate does not change formatting; changing a symbol or precision does, at once. The server caches the currencies
and drops the cache after every write to `transactioncurrency` or `organization` through the Core.

## RetrieveExchangeRate

`RetrieveExchangeRateRequest` with `TransactionCurrencyId` returns `ExchangeRate` (the currency's stored rate). On the Web
API: `GET RetrieveExchangeRate(TransactionCurrencyId=<guid>)` answers `{ "ExchangeRate": 1.1 }`. The caller needs Read on the
currency; an unknown id is 404. The message has an `sdkmessage` row, and plug-in steps can be registered on it without a table
filter (the read runs at stage 30 like `RetrieveMultiple`; the value is in `OutputParameters["ExchangeRate"]`).

## Existing databases

At startup, after the seed, `CurrencyColumnsUpgrade` visits every table that has a money column but no pair. It adds the
pair and the relationship, gives the `_base` columns their metadata, and sets `transactioncurrencyid` to the base currency and
`exchangerate` to 1 on every row without a currency (their `_base` already equals the value, which is right at rate 1). Each
table is done in one transaction. A second start finds nothing to do.

- The backfill is plain SQL: it is not audited, and it moves `versionnumber`, so a [change tracking](change-tracking.md) client
  sees each of those rows once more.
- Only tables that had no pair are backfilled. A table that already has the pair keeps its rows as they are.
- If a table already has a column that clashes with `transactioncurrencyid` or `exchangerate` but is not the right one,
  startup stops with an error naming it. Fix the table by hand.
- The seed (not the upgrade) adds the pair to `account` and `contact`, and a `usersettings` row for each user without one.

## Solutions

Currencies are data, not solution components: a solution never carries `transactioncurrency` rows. A money column in a file
brings the pair with it, the `TransactionCurrency_*` relationship of the file is not imported (system relationships are
derived), and an export writes the pair and the `_base` columns. See [Solutions](solutions.md).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **No name and symbol catalog.** Name and symbol are required for every currency. The ISO 4217 list in xrmverse may differ
  from Dataverse's.
- **Precision above 4 and rates below 1E-10 are refused** (see above).
- **Not modelled:** `transactioncurrency.organizationid` and `entityimage`, the tables of exchange-rate history and of base
  currency change, and every `usersettings` column except `transactioncurrencyid`. Reading one gives the usual "attribute not
  found" 400.
- **Error codes and texts.** The texts are ours. A few codes are our choice where Microsoft names none: a wrong custom code is
  `0x80048cf2`; a missing name, symbol or type, or a change of the base currency's code or type, is `InvalidArgument`; a null
  rate on the base currency is `0x80048cf5`. The checklist records the real ones.
- **Per-user formats.** FormattedValue uses en-US grouping and the organization's format. The user's number format and
  negative-currency setting are not modelled: a negative amount is always `-<symbol><amount>`.
- **Aggregates and audit.** A money aggregate (`SUM`, `AVG`) in FetchXML has no formatted value, and audit history shows money
  without a symbol.
- **DISTINCT queries.** Money in a record currency keeps the plain two-decimal format (no symbol), while `_base` gets the base
  symbol, so one distinct result can mix formats.
- **`_base` has no solution layer.** It follows its money column's managed properties, and uninstalling a managed solution
  does not restore the labels of its `_base` columns.
- **The upgrade.** It stops on a clashing column, does not audit its `versionnumber` bump, and does not backfill tables that
  already had the pair (see above).
- **Concurrency.** Picking the caller's `usersettings` currency takes no lock, so a change made at the same moment can be missed
  by one Create. The chosen currency is then re-read under a lock, and an inactive one falls back to the base currency.
- **Delete and use at the same moment.** Deleting a currency while another request puts that currency on a record or in a
  user's settings can end in a SQL Server deadlock. One request loses and gets a server error, not a 400. Nothing is written
  and no lookup is left dangling; repeat the request.
- **Stored money is not re-rounded** when a record's currency changes. Whether Dataverse does is in the checklist.
- **`xrmd server org add`** (the Admin API's organization create) always makes the euro the base currency.

## See also

- [Rollup columns](rollup-columns.md): Currency rollups
- [Supported queries](queries.md#known-deviations)
- [CLI xrmd](cli.md#currencies), [Web admin](web-admin.md#currencies)

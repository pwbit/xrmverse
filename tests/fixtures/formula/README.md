# Formula column values (plan 2.12c, Q1)

`values.json` holds what Dataverse gave for eleven formula columns (and, under `followup`, three more recorded afterwards on the
same rows) (Power Fx, `SourceType` 3) on eight invented rows. It is the
parity run Q1 of plan 2.12c: `formula-values.json` of that run, copied as recorded except the
`refused` texts, which are paraphrased (error codes kept).

- **Where:** a licensed test environment used only for these comparisons; solution `xrmCalcTest`, table `xrmv_calcorder` (the
  table of the 2.12a/2.12b parity exports).
- **When:** 2026-11-19, about 08:45 UTC.
- **Who:** a user whose language is Italian (`it-IT`, 1040). The Web API reads a formula in the caller's language, so the
  formulas were sent with `;` between arguments; `definitions_as_stored` is what `RetrieveAttribute` returned afterwards, with `,`.
- **How:** columns created through the Web API (`SourceType` 3, `FormulaDefinition`); values read back with the Web API.
- **Data:** invented test data (order codes, amounts, quantities, test account names); no customer data. Power Builders' own
  customizations on a licensed environment (legal guardrails §8).

Keys:

- `definitions_as_stored`: each column's formula as stored (no leading `=`). The related column is read through the lookup's
  navigation property, `xrmv_AccountId`.
- `source_type_mask`: the `SourceTypeMask` Dataverse returned.
- `refused`: definitions Dataverse refused, with its error code and the gist of its message (paraphrased).
- `followup`: `xrmv_fxchain` (`xrmv_fxdiv + 1`: a formula reading a formula column that failed reads it as blank),
  `xrmv_fxmax` (`Max(Blank(), -5)`) and `xrmv_fxmaxa` (`Max(xrmv_amount, -5)`): `Max` leaves a blank argument out. Each row is
  `[code, fxchain, fxmax, fxmaxa]`; the masks were not recorded.
- `rows`: the inputs (`code`, `amount`, `qty`, `account` = the name of the account the lookup points to) and each formula
  column's value (`fx…` = `xrmv_fx…`); `null` is an empty value. The last row has every input empty.

`FormulaCalculationTests.Q1_values_match_the_licensed_environment_run` (Xrmd.Integration.Tests) builds the same table on xrmverse
(`new_` names, the related column through `new_AccountId`), creates the eight rows and compares every value.
The findings are in `docs/formula-columns.md` (confirmed facts dated 2026-11-19).

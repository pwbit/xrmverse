# Plug-in execution context golden dumps (S.2e2c)

Each `.txt` is what `Xrmd.TestPlugins.ContextDumpPlugin` saw (and what the caller got back) in one scenario of
`tests/Xrmd.Integration.Tests/PluginContextGoldenTests.cs`. The plug-in is written against the public
`IPluginExecutionContext` to `IPluginExecutionContext7` members only and dumps every property of those interfaces in ordinal name
order: bags by ordinal key, values as `Type:value`. The known SDK types (Entity, EntityReference, EntityCollection, OptionSetValue,
Money, AliasedValue, ColumnSet, QueryExpression, QueryByAttribute, FetchExpression, Relationship, BooleanManagedProperty, the bags and
image collections) are written by explicit writers that name only their public data members; reflection is the fallback for any other
type. A new SDK version therefore changes a golden only when a data member the writers name changes.

## Normalisation (done by the test, per scenario, over the whole file)
- Ids are written raw by the plug-in. Known ids get role names: `{user}` (admin), `{businessunit}`, `{organization}`, `{useraad}` and,
  in `runas_user`, `{runasuser}`. Every other id is numbered `{id1}`, `{id2}`... in first-seen order for the whole scenario, so an id
  that drifts between stages or steps shows. `{empty}` is the empty id. A wrong but self-consistent user or organization id fails.
- Dates are `{date}`, row versions and `versionnumber` are `{rowversion}`.
- Identity tokens: the plug-in writes `@obj...` after each mutable reference object (context, entity, reference, collection, bag,
  image, option set, money) from a process-wide `ConditionalWeakTable`; the test renumbers them `@1`, `@2`... in first-seen order. The same
  `@n` is the same instance; a copy gets a new number.
- Inside a section dumps are ordered by name, then by content with ids and tokens blanked, then by creation order (`Seq:` line, removed).

## Same-instance relations frozen today
- `create_pre_mutates_target`, `in_place_mutations`: the `InputParameters`, `OutputParameters` and `SharedVariables` bags and the `Target`
  entity (until it is replaced) are one instance across stages 10, 20 and 40; each step gets its own context, `OwningExtension` and image
  collections.
- `merged_bulk_views`: on a single Create, `Targets[0]` of the CreateMultiple step is the Create step's `Target`; in an UpdateMultiple the
  inner Update `Target` is the outer `Targets[i]`, and the inner `ParentContext` holds that same `Targets` collection.
- `nested_parent_chain`, `runas_user`: the child's `ParentContext.InputParameters["Target"]` is the parent's own `Target`.
- `in_place_mutations`: a replaced `Target` is what the later steps see and what is stored; the caller's own entity keeps the original.

## Provenance
Captured from the code before the pipeline moved onto the model context (base commit d4185dea, plan S.2e2c Task 1), recaptured in the
fix round of that task. Same write-if-missing rule as the S.2e1 goldens: a missing file is written once and the test fails; an existing
file is never rewritten. They are the contract of the move: what a plug-in sees must not change. `.gitattributes` disables line-ending
conversion for this directory.

Mutation grammar of the plug-in config (`{entity}|{mutation}`, entity `-` = mutate only): `attr=value`, `attr=-` (remove),
`attr=@option:N` (change an `OptionSetValue` in place), `Target=replace:marker` (replace the Target instance, same logical name and id,
copied attributes, `marker="replaced"`). After a dump the stage is appended to the shared variable `dumped`.

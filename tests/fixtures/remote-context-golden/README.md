# Remote execution context golden files

`async-context.xml` is `AsyncContextSerializer.Serialize` (UTF-8, no BOM) and `webhook.json` is `WebhookPayload.Serialize` of the fixed
`RemoteExecutionContext` built by `tests/Xrmd.Pipeline.Tests/Sdk/RemoteContextGoldenTests.cs` (message Update, stage 40, mode 1,
depth 2, every value type in the target, pre/post images, output parameters, shared variables, a parent context).

They were captured from the SDK-based serializers on the S.2e1 branch (base commit 9d37111d) by running that test once with the files
missing. They are a contract: S.2e2 (the own writer) must produce these bytes exactly. Compare bytes, not text; `.gitattributes`
disables line-ending conversion for this directory. The test never rewrites an existing file; to recapture, delete a file on purpose
and review the diff.

Captured with the SDK types of Microsoft.PowerPlatform.Dataverse.Client 1.2.27 (referenced by Xrmd.Abstractions) on .NET 10; the SDK
DataContract output is what is frozen here.

Not covered, by design: `object[]` (also a nested mixed array) and `BooleanManagedProperty`-style unknown SDK types. The async
serializer refuses `System.Object[]` ("Type 'System.Object[]' is not allowed", the SdkTypeAllowlist), so it cannot appear in a context
that both serializers write. Typed arrays (`EntityReference[]`, `Entity[]`, `OptionSetValue[]`, `Money[]`), a bare `decimal`, key
attributes, entity state, related entities, aliased values wrapping a record and a collection, a paged collection (cookie, total
count), a `ColumnSet`, a `Relationship`, an `EntityReference` with key attributes and row version, and a `QueryExpression` are covered.

## Query goldens (S.2e2b)

`query-expression.xml` (a kitchen-sink `QueryExpression` including a `SubQueryExpression`, a null condition value, a multi-value `In` condition and one condition with string, long, double, decimal, bool, byte[] and string[] values; the SDK serializer accepts all of them),
`query-by-attribute.xml` (a `QueryByAttribute` with an `EntityReference` value) and `fetch.xml` (a `FetchExpression`) are
`AsyncContextSerializer.SerializeQuery` (UTF-8, no BOM) of the fixed queries built in `RemoteContextGoldenTests`. They were captured
from today's DataContractSerializer-based code (before the own writer exists) with the same write-if-missing rule, and are the
contract the own query writer must reproduce byte for byte.

Not in the query golden: an `int[]` condition value (for example `AddCondition("code", In, new[] { 1, 2, 3 })`, which becomes one
`int[]` value). Today's `SerializeQuery` throws `ArgumentException: Invalid name character in 'Int32[]'` on it, and the own code
refuses it (`ModelCanCarry` is false), so it stays on the SDK path.

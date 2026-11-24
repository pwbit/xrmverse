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

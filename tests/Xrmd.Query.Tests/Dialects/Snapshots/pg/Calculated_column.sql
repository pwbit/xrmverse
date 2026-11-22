SELECT t0."xrmv_calcorderid" AS c0, (SELECT CASE WHEN abs(round(tc.v, 4)) < 1000000000000000 THEN CAST(tc.v AS numeric(19,4)) END FROM (VALUES (CAST(round(CAST((t0."xrmv_amount" * CAST(t0."xrmv_quantity" AS decimal(10, 0))) AS numeric), 2) AS numeric))) AS tc(v)) AS c1, t0."transactioncurrencyid" AS c2
FROM "data"."xrmv_calcorder" AS t0
ORDER BY t0."xrmv_calcorderid" ASC LIMIT (@top)

-- parameters
-- @top Int32 = 5001

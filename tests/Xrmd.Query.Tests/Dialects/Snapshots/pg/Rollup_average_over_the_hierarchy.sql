WITH RECURSIVE tg AS (SELECT t."xrmv_rollacctid" AS id, 2 AS prec, t."exchangerate" AS rate, CASE WHEN t."transactioncurrencyid" IS NULL OR t."exchangerate" IS NULL THEN 0 ELSE 1 END AS hascurrency FROM "data"."xrmv_rollacct" AS t),
tree AS (SELECT tg.id AS root, tg.id AS node, 0 AS depth, 0 AS revisit FROM tg UNION ALL SELECT tree.root, ch."xrmv_rollacctid", tree.depth + 1, CASE WHEN ch."xrmv_rollacctid" = tree.root THEN 1 ELSE 0 END FROM tree JOIN "data"."xrmv_rollacct" AS ch ON ch."xrmv_parentid" = tree.node WHERE tree.revisit = 0 AND tree.depth <= 100),
nodes AS (SELECT root, node FROM tree WHERE revisit = 0 AND depth <= 100),
walk AS (SELECT root, MAX(revisit) AS revisit, MAX(depth) AS depth FROM tree GROUP BY root),
agg AS (SELECT n.root AS id, AVG(CAST(COALESCE((CAST(s."xrmv_budget" AS decimal(38, 10)) / NULLIF(CAST(s."exchangerate" AS decimal(38, 10)), 0)), 0) AS decimal(38, 10))) AS value FROM nodes AS n JOIN tg ON tg.id = n.root JOIN "data"."xrmv_rollacct" AS s ON s."xrmv_rollacctid" = n.node GROUP BY n.root),
res AS (SELECT tg.id, CASE WHEN st.state = 1 THEN (SELECT CASE WHEN abs(round(tc.v, 4)) < 1000000000000000 THEN CAST(tc.v AS numeric(19,4)) END FROM (VALUES (CAST(x.v AS numeric))) AS tc(v)) END AS value, CASE WHEN st.state = 1 THEN CAST(xb.b AS decimal(19, 4)) END AS base, st.state AS state FROM tg LEFT JOIN agg ON agg.id = tg.id LEFT JOIN walk ON walk.root = tg.id CROSS JOIN LATERAL (SELECT CAST(round(CAST(COALESCE(agg.value, 0) AS numeric), 4) AS decimal(38, 4)) AS b) AS xb CROSS JOIN LATERAL (SELECT (SELECT CASE WHEN abs(round(tc.v, 4)) < 1000000000000000000000000 THEN CAST(tc.v AS numeric(28,4)) END FROM (VALUES (CAST(xb.b AS numeric))) AS tc(v)) * CAST(tg.rate AS decimal(28, 10)) AS v) AS x CROSS JOIN LATERAL (SELECT CASE WHEN walk.revisit = 1 THEN 6 WHEN walk.depth > 100 THEN 5 WHEN tg.hascurrency = 0 THEN 7 WHEN xb.b < @basemin OR xb.b > @basemax THEN 2 WHEN x.v IS NOT NULL AND (SELECT CASE WHEN abs(round(tc.v, 4)) < 1000000000000000 THEN CAST(tc.v AS numeric(19,4)) END FROM (VALUES (CAST(x.v AS numeric))) AS tc(v)) IS NULL THEN 2 ELSE 1 END AS state) AS st)
UPDATE "data"."xrmv_rollacct" AS t SET "xrmv_tree" = res.value, "xrmv_tree_base" = res.base, "xrmv_tree_date" = @now, "xrmv_tree_state" = res.state
FROM res
WHERE res.id = t."xrmv_rollacctid" AND (EXISTS (SELECT t."xrmv_tree", t."xrmv_tree_base", t."xrmv_tree_state" EXCEPT SELECT res.value, res.base, res.state))
RETURNING t."xrmv_rollacctid", t."xrmv_tree", t."xrmv_tree_base", t."xrmv_tree_date", t."xrmv_tree_state";

-- parameters
-- @basemin Decimal = -922337203685477
-- @basemax Decimal = 922337203685477
-- @now DateTime2 = 2026-11-19T10:30:00.0000000Z

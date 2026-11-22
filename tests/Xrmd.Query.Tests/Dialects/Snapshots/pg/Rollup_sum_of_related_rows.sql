WITH RECURSIVE tg AS (SELECT t."xrmv_rollacctid" AS id FROM "data"."xrmv_rollacct" AS t),
nodes AS (SELECT id AS root, id AS node FROM tg),
agg AS (SELECT n.root AS id, SUM(CAST(trunc(CAST(r."xrmv_amount" AS numeric), 2) AS decimal(38, 2))) AS value FROM nodes AS n JOIN "data"."xrmv_rollorder" AS r ON r."xrmv_acctid" = n.node WHERE (r."xrmv_status" = 2) GROUP BY n.root),
res AS (SELECT tg.id, CASE WHEN st.state = 1 THEN (SELECT CASE WHEN abs(round(tc.v, 2)) < 1000000000000000000000 THEN CAST(tc.v AS numeric(23,2)) END FROM (VALUES (CAST(x.v AS numeric))) AS tc(v)) END AS value, st.state AS state FROM tg LEFT JOIN agg ON agg.id = tg.id CROSS JOIN LATERAL (SELECT CAST(COALESCE(agg.value, 0) AS decimal(38, 2)) AS v) AS x CROSS JOIN LATERAL (SELECT CASE WHEN x.v IS NOT NULL AND (SELECT CASE WHEN abs(round(tc.v, 2)) < 1000000000000000000000 THEN CAST(tc.v AS numeric(23,2)) END FROM (VALUES (CAST(x.v AS numeric))) AS tc(v)) IS NULL THEN 2 ELSE 1 END AS state) AS st)
UPDATE "data"."xrmv_rollacct" AS t SET "xrmv_total" = res.value, "xrmv_total_date" = @now, "xrmv_total_state" = res.state
FROM res
WHERE res.id = t."xrmv_rollacctid" AND (EXISTS (SELECT t."xrmv_total", t."xrmv_total_state" EXCEPT SELECT res.value, res.state))
RETURNING t."xrmv_rollacctid", t."xrmv_total", CAST(NULL AS decimal(19, 4)), t."xrmv_total_date", t."xrmv_total_state";

-- parameters
-- @now DateTime2 = 2026-11-19T10:30:00.0000000Z

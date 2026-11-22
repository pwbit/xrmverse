SELECT CAST(EXTRACT(month FROM t0."pb_startdate") AS integer) AS c0, (CAST(floor((EXTRACT(doy FROM t0."pb_startdate") + EXTRACT(dow FROM date_trunc('year', t0."pb_startdate")) - 1) / 7) AS integer) + 1) AS c1, COUNT(*) AS c2, CAST(SUM(CAST(t0."pb_count" AS bigint)) AS bigint) AS c3
FROM "data"."pb_project" AS t0
GROUP BY CAST(EXTRACT(month FROM t0."pb_startdate") AS integer), (CAST(floor((EXTRACT(doy FROM t0."pb_startdate") + EXTRACT(dow FROM date_trunc('year', t0."pb_startdate")) - 1) / 7) AS integer) + 1)
ORDER BY c0 ASC, c1 ASC LIMIT (@top)

-- parameters
-- @top Int32 = 5001

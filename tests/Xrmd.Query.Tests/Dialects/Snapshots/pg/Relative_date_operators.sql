SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1
FROM "data"."pb_project" AS t0
WHERE ((t0."pb_startdate" >= @p0 AND t0."pb_startdate" <= @p1) AND (t0."pb_startdate" >= @p2 AND t0."pb_startdate" < @p3))
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @p0 DateTime2 = 2026-11-12T00:00:00.0000000Z
-- @p1 DateTime2 = 2026-11-19T10:30:00.0000000Z
-- @p2 DateTime2 = 2026-11-01T00:00:00.0000000Z
-- @p3 DateTime2 = 2026-12-01T00:00:00.0000000Z
-- @top Int32 = 5001

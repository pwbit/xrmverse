SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1
FROM "data"."pb_project" AS t0
WHERE ((t0."versionnumber" > @p0 AND t0."versionnumber" < @p1))
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @p0 Int64 = 100
-- @p1 Int64 = 250
-- @top Int32 = 5001

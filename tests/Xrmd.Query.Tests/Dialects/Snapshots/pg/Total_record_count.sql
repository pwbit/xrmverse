SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1, COUNT(*) OVER() AS c2
FROM "data"."pb_project" AS t0
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @top Int32 = 26

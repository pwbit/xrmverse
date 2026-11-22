SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1
FROM "data"."pb_project" AS t0
WHERE t0."pb_projectid" > @last
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @last Guid = 44444444-4444-4444-4444-444444444444
-- @top Int32 = 11

SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1, t0."pb_count" AS c2
FROM "data"."pb_project" AS t0
WHERE (t0."pb_count" > @p0 AND t0."pb_name" LIKE @p1)
ORDER BY t0."pb_name" DESC, t0."pb_projectid" ASC
OFFSET (@offset) LIMIT (@fetch)

-- parameters
-- @p0 Int32 = 1
-- @p1 String = a\_%
-- @offset Int32 = 100
-- @fetch Int32 = 51

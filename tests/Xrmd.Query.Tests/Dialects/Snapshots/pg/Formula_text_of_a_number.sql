SELECT t0."xrmv_fxorderid" AS c0, CAST(left(CAST(CAST(t0."xrmv_qty" AS bigint) AS citext), 4000) AS citext) AS c1
FROM "data"."xrmv_fxorder" AS t0
ORDER BY t0."xrmv_fxorderid" ASC LIMIT (@top)

-- parameters
-- @top Int32 = 5001

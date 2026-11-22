SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1, t1."fullname" AS c2
FROM "data"."pb_project" AS t0
LEFT OUTER JOIN "data"."systemuser" AS t1 ON t1."systemuserid" = t0."pb_customerid" AND (t1."fullname" LIKE @p0)
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @p0 String = Ad%
-- @top Int32 = 5001

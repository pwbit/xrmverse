SELECT t0."xrmv_fxorderid" AS c0, CAST(left(CAST(CONCAT(CAST(UPPER(CAST(left(t0."xrmv_code", 2) AS citext)) AS citext), CAST(CASE WHEN (2 * length(@p0)) = 0 OR COALESCE(c1."v", 1073741824) = 1073741824 THEN t0."xrmv_code" ELSE overlay(t0."xrmv_code" placing @p1 from CAST(c1."v" AS integer) for CAST(c0."v" AS integer)) END AS citext)) AS citext), 4000) AS citext) AS c1, (SELECT CASE WHEN abs(round(tc.v, 2)) < 1000000000000000000000000000000000000 THEN CAST(tc.v AS numeric(38,2)) END FROM (VALUES (CAST(round(CAST(CASE WHEN ABS(c2."v") <= 100000000000 THEN CAST(c2."v" AS decimal(14, 2)) END AS numeric), 2) AS numeric))) AS tc(v)) AS c2, (SELECT CASE WHEN tc.v > -2147483649 AND tc.v < 2147483648 THEN CAST(trunc(tc.v) AS integer) END FROM (VALUES (CAST((length(rtrim(CONCAT(t0."xrmv_code", '.'), ' ')) - 1) AS numeric))) AS tc(v)) AS c3
FROM "data"."xrmv_fxorder" AS t0 LEFT JOIN LATERAL (SELECT MAX(x.v) AS "v" FROM (VALUES ((length(rtrim(CONCAT(@p0, '.'), ' ')) - 1))) AS x(v)) AS c0 ON true LEFT JOIN LATERAL (SELECT MAX(x.v) AS "v" FROM (VALUES ((data.strpos(t0."xrmv_code", CAST(@p0 AS text) COLLATE "C", (strpos(t0."xrmv_code", CAST(@p0 AS text) COLLATE "C") + 1073741823) % 1073741824 + 1 + c0."v") + 1073741823) % 1073741824 + 1)) AS x(v)) AS c1 ON true LEFT JOIN LATERAL (SELECT MAX(x.v) AS "v" FROM (VALUES (round(CAST(CAST(t0."xrmv_amount" AS decimal(24, 2)) AS numeric), 1))) AS x(v)) AS c2 ON true
ORDER BY t0."xrmv_fxorderid" ASC LIMIT (@top)

-- parameters
-- @p0 String = a
-- @p1 String = b
-- @top Int32 = 5001

SELECT DISTINCT CAST(MIN(CAST(t0."pb_name" AS text) COLLATE "C") OVER (PARTITION BY (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)) AS citext) AS c0, t0."pb_count" AS c1
FROM "data"."pb_project" AS t0
ORDER BY CAST(MIN(CAST(t0."pb_name" AS text) COLLATE "C") OVER (PARTITION BY (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)) AS citext) ASC, c1 ASC LIMIT (@top)

-- parameters
-- @top Int32 = 5001

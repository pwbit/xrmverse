SELECT CAST(MIN(CAST(t0."pb_name" AS text) COLLATE "C") AS citext) AS c0, COUNT(DISTINCT (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)) AS c1, COUNT(*) AS c2
FROM "data"."pb_project" AS t0
GROUP BY (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)
ORDER BY (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) ASC, c0 ASC LIMIT (@top)

-- parameters
-- @top Int32 = 5001

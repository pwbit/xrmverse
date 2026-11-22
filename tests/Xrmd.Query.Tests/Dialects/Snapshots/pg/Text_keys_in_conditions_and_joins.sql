SELECT t0."pb_projectid" AS c0, t0."pb_name" AS c1
FROM "data"."pb_project" AS t0
INNER JOIN "data"."xrmv_calcorder" AS t1 ON (replace(replace(rtrim(t1."xrmv_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) = (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)
WHERE ((replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) = (replace(replace(rtrim(@p0::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) AND (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) <> (replace(replace(rtrim(@p1::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) AND (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) IN ((replace(replace(rtrim(@p2::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as), (replace(replace(rtrim(@p3::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)) AND (replace(replace(rtrim(t0."pb_name"::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) IN (SELECT (replace(replace(rtrim(value::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as) FROM (SELECT CAST(e.value #>> '{}' AS citext) AS "value" FROM jsonb_array_elements(@p4::jsonb) AS e(value))) AND t0."pb_count" = @p5 AND t0."pb_name" LIKE @p6)
ORDER BY t0."pb_projectid" ASC LIMIT (@top)

-- parameters
-- @p0 String = Acme 
-- @p1 String = Beta
-- @p2 String = a
-- @p3 String = b
-- @p4 String = ["v0","v1","v2","v3","v4","v5","v6","v7","v8","v9","v10","v11","v12","v13","v14","v15","v16","v17","v18","v19","v20","v21","v22","v23","v24","v25","v26","v27","v28","v29","v30","v31","v32","v33","v34","v35","v36","v37","v38","v39","v40","v41","v42","v43","v44","v45","v46","v47","v48","v49","v50","v51","v52","v53","v54","v55","v56","v57","v58","v59","v60","v61","v62","v63","v64","v65","v66","v67","v68","v69","v70","v71","v72","v73","v74","v75","v76","v77","v78","v79","v80","v81","v82","v83","v84","v85","v86","v87","v88","v89","v90","v91","v92","v93","v94","v95","v96","v97","v98","v99","v100"]
-- @p5 Int32 = 3
-- @p6 String = Ac%
-- @top Int32 = 5001

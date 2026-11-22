-- Webhook secrets (plan 3.2): the write-only authvalue of a serviceendpoint (a key, or the settings XML of its headers or query
-- parameters) lives here, outside data.serviceendpoint, so that no read path (Retrieve, queries, FetchXML, TDS, change
-- tracking, the Web API) can return it. One row per endpoint that has a value; the row goes with the endpoint.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.serviceendpoint_secret', N'U') IS NULL
    CREATE TABLE meta.serviceendpoint_secret (
        serviceendpointid uniqueidentifier NOT NULL CONSTRAINT PK_meta_serviceendpoint_secret PRIMARY KEY,
        authvalue         nvarchar(2048)   NOT NULL
    );
GO

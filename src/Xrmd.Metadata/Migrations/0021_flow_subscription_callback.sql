-- Flow subscription callbacks (plan X.12b): the callback URL of a webhook subscription of the connector trigger carries the flow's
-- signature, so it lives here, outside the data table xrmverse_flowsubscription, where no read path (Retrieve, queries, FetchXML,
-- TDS, change tracking, the Web API, audit) can return it. One row per subscription; the row goes with the subscription.
-- Every CREATE is guarded by an existence check, so the script can be applied again without failing.

IF OBJECT_ID(N'meta.flow_subscription_callback', N'U') IS NULL
    CREATE TABLE meta.flow_subscription_callback (
        subscriptionid uniqueidentifier NOT NULL,
        callbackurl    nvarchar(2048)   NOT NULL,
        CONSTRAINT PK_meta_flow_subscription_callback PRIMARY KEY (subscriptionid)
    );
GO

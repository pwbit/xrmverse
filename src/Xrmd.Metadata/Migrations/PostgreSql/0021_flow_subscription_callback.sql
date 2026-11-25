-- Flow subscription callbacks (plan X.12b): see the SQL Server migration of the same name. One row per subscription, outside the data table.

CREATE TABLE IF NOT EXISTS meta.flow_subscription_callback (
    subscriptionid uuid           NOT NULL,
    callbackurl    varchar(2048)  NOT NULL,
    CONSTRAINT "PK_meta_flow_subscription_callback" PRIMARY KEY (subscriptionid)
);

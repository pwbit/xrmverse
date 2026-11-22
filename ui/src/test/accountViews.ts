// Two views of account written by hand for the view tests, in the shape of a public view and of a quick find view
// (layout and fetch, as a savedquery row holds them). They replace the views of the OpenProject365 export, which is not
// public (eng/public-export/manifest.txt).

/** A public view: customers, with a choice, a text and a lookup column. */
export const CUSTOMERS_LAYOUT = `<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">
  <cell name="name" width="250" /><cell name="op365_customertype" width="120" /><cell name="op365_vatnumber" width="130" />
  <cell name="primarycontactid" width="180" /></row></grid>`;
export const CUSTOMERS_FETCH = `<fetch version="1.0" mapping="logical"><entity name="account">
  <attribute name="name" /><attribute name="op365_customertype" /><attribute name="op365_vatnumber" /><attribute name="primarycontactid" />
  <attribute name="accountid" />
  <order attribute="name" descending="false" />
  <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
  <filter type="and"><condition attribute="op365_iscustomer" operator="eq" value="1" /></filter>
</entity></fetch>`;

/** A quick find view: three quick find columns. */
export const QUICK_FIND_LAYOUT = `<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">
  <cell name="name" width="250" /><cell name="op365_vatnumber" width="130" /><cell name="primarycontactid" width="180" /></row></grid>`;
export const QUICK_FIND_FETCH = `<fetch version="1.0" mapping="logical"><entity name="account">
  <attribute name="name" /><attribute name="op365_vatnumber" /><attribute name="primarycontactid" /><attribute name="accountid" />
  <order attribute="name" descending="false" />
  <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
  <filter type="or" isquickfindfields="1">
    <condition attribute="name" operator="like" value="{0}" />
    <condition attribute="op365_vatnumber" operator="like" value="{0}" />
    <condition attribute="primarycontactid" operator="like" value="{0}" />
  </filter>
</entity></fetch>`;

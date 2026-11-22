import { childElements, parseXml } from '../../parsers/xml';
import type { FormRow } from './formDesign';

/** A `systemform` row of the table `account` holding `formxml`. */
export function formRow(formxml: string | null, extra: Partial<FormRow> = {}): FormRow {
  return { systemformid: 'f1', name: 'Information', objecttypecode: 'account', type: 2, formxml, ...extra };
}

/** Each `<tag>` child of the `<form>` of `xml`, serialized, in document order. */
export function blocksOf(xml: string, tag: string): string[] {
  const form = parseXml(xml).documentElement;
  return childElements(form, tag).map((el) => new XMLSerializer().serializeToString(el));
}

/** The blocks of `<form>` the designer never edits. */
export const KEPT_BLOCKS = ['events', 'formLibraries', 'Navigation', 'DisplayConditions', 'footer', 'controlDescriptions'] as const;

/**
 * A FormXML with every block the designer keeps, unknown attributes and elements on the modeled ones, and labels in a
 * language the UI does not speak.
 */
export const RICH_FORM = `<form shownavigationbar="true" maxWidth="1920" xmlns:x="urn:x">
  <tabs showlabels="maybe">
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}" expanded="true" showlabel="true" verticallayout="true" locklevel="0" x:extra="1">
      <labels>
        <label description="Allgemein" languagecode="1031" />
        <label description="General" languagecode="1033" />
      </labels>
      <columns>
        <column width="50%" unknown="u">
          <sections>
            <section name="main" showlabel="true" showbar="false" columns="2" id="{a0000000-0000-0000-0000-000000000002}" labelwidth="115">
              <labels>
                <label description="Main" languagecode="1033" />
                <label description="Haupt" languagecode="1031" />
              </labels>
              <rows>
                <row height="2">
                  <cell id="{a0000000-0000-0000-0000-000000000003}" showlabel="true" locklevel="0">
                    <labels>
                      <label description="Account Name" languagecode="1033" />
                    </labels>
                    <control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name" disabled="false" />
                  </cell>
                  <cell id="{a0000000-0000-0000-0000-000000000004}">
                    <labels>
                      <label description="Phone" languagecode="1033" />
                    </labels>
                    <control id="telephone1" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="telephone1" />
                  </cell>
                </row>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000005}" colspan="2">
                    <labels>
                      <label description="Contacts" languagecode="1033" />
                    </labels>
                    <control id="contacts" classid="{E7A81278-8635-4D9E-8D4D-59480B391C5B}" indicationOfSubgrid="true">
                      <parameters>
                        <RelationshipName>contact_customer_accounts</RelationshipName>
                      </parameters>
                    </control>
                  </cell>
                </row>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000006}">
                    <labels>
                      <label description="Fax" languagecode="1033" />
                    </labels>
                    <control id="fax" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="fax" />
                  </cell>
                  <cell id="{a0000000-0000-0000-0000-000000000007}" showlabel="false">
                    <labels>
                      <label description="" languagecode="1033" />
                    </labels>
                  </cell>
                </row>
              </rows>
            </section>
          </sections>
        </column>
        <column width="50%">
          <sections>
            <section name="side" showlabel="false" columns="1" id="{a0000000-0000-0000-0000-000000000008}">
              <labels>
                <label description="Side" languagecode="1033" />
              </labels>
              <rows>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000009}">
                    <labels>
                      <label description="Custom" languagecode="1033" />
                    </labels>
                    <control id="pcf" classid="{F9A8A302-114E-466A-B582-6771B2AE0D92}" uniqueid="{b0000000-0000-0000-0000-000000000001}" />
                  </cell>
                </row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
    <tab name="details" id="{a0000000-0000-0000-0000-00000000000a}" expanded="false" showlabel="true">
      <labels>
        <label description="Details" languagecode="1033" />
      </labels>
      <columns>
        <column width="100%">
          <sections>
            <section name="more" showlabel="true" columns="1" id="{a0000000-0000-0000-0000-00000000000b}">
              <labels>
                <label description="More" languagecode="1033" />
              </labels>
              <rows />
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
  <header id="{a0000000-0000-0000-0000-00000000000c}" columns="1111" celllabelposition="Top">
    <rows>
      <row>
        <cell id="{a0000000-0000-0000-0000-00000000000d}">
          <labels>
            <label description="Owner" languagecode="1033" />
          </labels>
          <control id="header_ownerid" classid="{270BD3DB-D9AF-4782-9025-509E298DEC0A}" datafieldname="ownerid" />
        </cell>
      </row>
    </rows>
  </header>
  <footer id="{a0000000-0000-0000-0000-00000000000e}">
    <rows>
      <row>
        <cell id="{a0000000-0000-0000-0000-00000000000f}">
          <control id="footer_modifiedon" classid="{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}" datafieldname="modifiedon" />
        </cell>
      </row>
    </rows>
  </footer>
  <events>
    <event name="onload" application="false" active="false">
      <Handlers>
        <Handler functionName="App.onLoad" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000001}" enabled="true" parameters="" passExecutionContext="true" />
      </Handlers>
    </event>
    <event name="onchange" application="false" active="false" attribute="name">
      <Handlers>
        <Handler functionName="App.onName" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000002}" enabled="true" />
      </Handlers>
    </event>
  </events>
  <formLibraries>
    <Library name="new_app.js" libraryUniqueId="{c0000000-0000-0000-0000-000000000003}" />
  </formLibraries>
  <Navigation>
    <NavBar>
      <NavBarByRelationshipItem RelationshipName="contact_customer_accounts" Id="nav_contacts" Sequence="10" Area="Details">
        <Titles>
          <Title LCID="1033" Text="Contacts" />
        </Titles>
      </NavBarByRelationshipItem>
    </NavBar>
  </Navigation>
  <controlDescriptions>
    <controlDescription forControl="{b0000000-0000-0000-0000-000000000001}">
      <customControl name="new_Custom.Control" formFactor="0">
        <parameters />
      </customControl>
    </controlDescription>
  </controlDescriptions>
  <DisplayConditions Order="1" FallbackForm="true">
    <Everyone />
  </DisplayConditions>
  <unknownBlock a="b"><inner /></unknownBlock>
</form>`;

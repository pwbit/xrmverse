import { render, screen } from '@testing-library/react';
import { useState, type ComponentType } from 'react';
import { mockApps } from '../../apps/testSupport';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { attr } from '../tables/testAttrs';
import type { DesignCardProps } from './designerSlots';
import { readView, type ViewDesign } from './viewDesign';
import { ACCOUNT } from './viewDesignTestSupport';

/** The account columns the filter and find column editors are tested with: one or more of every E5 kind. */
export const ACCOUNT_ATTRIBUTES = [
  attr('accountid', { kind: 'unsupported', typeName: 'Uniqueidentifier', displayName: 'Account', isPrimaryId: true }),
  attr('name', { displayName: 'Account Name', isPrimaryName: true }),
  attr('address1_city', { displayName: 'City' }),
  attr('description', { kind: 'memo', typeName: 'Memo', displayName: 'Description' }),
  attr('revenue', { kind: 'money', typeName: 'Money', displayName: 'Annual Revenue' }),
  attr('numberofemployees', { kind: 'int', typeName: 'Integer', displayName: 'Employees' }),
  attr('createdon', { kind: 'datetime', typeName: 'DateTime', displayName: 'Created On' }),
  attr('statuscode', {
    kind: 'choice',
    typeName: 'Status',
    displayName: 'Status',
    options: [{ value: 1, label: 'Active' }, { value: 2, label: 'Inactive' }],
  }),
  attr('donotemail', {
    kind: 'bool',
    typeName: 'Boolean',
    displayName: 'Do not Email',
    options: [{ value: 0, label: 'Allow' }, { value: 1, label: 'Do Not Allow' }],
  }),
  attr('primarycontactid', { kind: 'lookup', typeName: 'Lookup', displayName: 'Primary Contact', targets: ['contact'] }),
  attr('ownerid', { kind: 'lookup', typeName: 'Owner', displayName: 'Owner', targets: ['systemuser', 'team'] }),
  attr('owningbusinessunit', { kind: 'lookup', typeName: 'Lookup', displayName: 'Owning Business Unit', targets: ['businessunit'] }),
  attr('primarycontactidname', { displayName: 'Primary Contact Name', attributeOf: 'primarycontactid' }),
  attr('documentbody', { displayName: 'Document', hidden: true }),
];

const LAYOUT = '<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid"><cell name="name" width="150"/></row></grid>';

/** A design of an account view whose `<entity>` holds `inner` after its attributes. */
export function accountView(inner = '', querytype = 0): ViewDesign {
  const fetchxml = `<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name"/><attribute name="accountid"/>${inner}</entity></fetch>`;
  return readView({ name: 'V', querytype, isdefault: false, fetchxml, layoutxml: LAYOUT, returnedtypecode: 'account' }, ACCOUNT);
}

/**
 * Renders `Editor` with the design kept in state (after the sign-in gate opens); `latest()` is the design after the last
 * change.
 */
export async function renderEditor(Editor: ComponentType<DesignCardProps>, initial: ViewDesign) {
  mockApps();
  let latest = initial;
  function Harness() {
    const [design, setDesign] = useState(initial);
    latest = design;
    return (
      <div data-testid="editor">
        <Editor design={design} attributes={ACCOUNT_ATTRIBUTES} onChange={setDesign} />
      </div>
    );
  }
  render(
    <Providers>
      <Harness />
    </Providers>,
  );
  await screen.findByTestId('editor');
  return { latest: () => latest };
}

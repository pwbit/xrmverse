import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { en } from '../../shell/i18n/en';
import { FindColumnsEditor } from './FindColumnsEditor';
import { accountView, renderEditor } from './filterEditorTestSupport';
import { writeView } from './viewDesign';

const QUICK_FIND =
  '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter>' +
  '<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/></filter>';

const list = () => screen.getByRole('list', { name: en['views.find.title'] });
const listed = () => within(list()).queryAllByRole('listitem').map((li) => li.textContent);
const addSelect = () => screen.getByRole('combobox', { name: en['views.find.add'] });
const offered = () => within(addSelect()).getAllByRole('option').map((o) => (o as HTMLOptionElement).value).filter(Boolean);

describe('FindColumnsEditor', () => {
  it('lists the find columns and offers only text, number and lookup columns not yet used', async () => {
    await renderEditor(FindColumnsEditor, accountView(QUICK_FIND, 4));
    expect(listed()).toEqual(['Account Name']);
    expect(offered().sort()).toEqual(
      ['address1_city', 'description', 'numberofemployees', 'owningbusinessunit', 'ownerid', 'primarycontactid', 'revenue'].sort(),
    );
  });

  it('adds and removes like {0} conditions of the quick find filter', async () => {
    const { latest } = await renderEditor(FindColumnsEditor, accountView(QUICK_FIND, 4));
    await userEvent.selectOptions(addSelect(), 'address1_city');
    expect(listed()).toEqual(['Account Name', 'City']);
    expect(offered()).not.toContain('address1_city');
    expect(latest().findColumns).toEqual(['name', 'address1_city']);
    expect(writeView(latest()).fetchxml).toContain(
      '<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/>' +
        '<condition attribute="address1_city" operator="like" value="{0}"/></filter>',
    );

    await userEvent.click(screen.getByRole('button', { name: 'Remove Account Name from the find columns' }));
    expect(listed()).toEqual(['City']);
    const fetch = writeView(latest()).fetchxml;
    expect(fetch).toContain('<filter type="or" isquickfindfields="1"><condition attribute="address1_city" operator="like" value="{0}"/></filter>');
    expect(fetch).toContain('<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter>');
  });

  it('starts the quick find filter when the view has none, and says when it is empty', async () => {
    const { latest } = await renderEditor(FindColumnsEditor, accountView('', 4));
    expect(screen.getByText(en['views.find.empty'])).toBeInTheDocument();
    await userEvent.selectOptions(addSelect(), 'primarycontactid');
    expect(writeView(latest()).fetchxml).toContain(
      '<filter type="or" isquickfindfields="1"><condition attribute="primarycontactid" operator="like" value="{0}"/></filter>',
    );
  });
});

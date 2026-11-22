import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { en } from '../../shell/i18n/en';
import { onPath } from '../security/testSupport';
import {
  canvasReady, FORM, FORM_ID, installControls, openForm, PAGE_TEST_TIMEOUT, propsCard, saveButton, savedForm, section, type Call,
} from './controlsTestSupport';

/** The Events card and the OnChange handlers of a field (G6). */

const EVENTS = `
  <formLibraries>
    <Library name="new_a.js" libraryUniqueId="{e0000000-0000-0000-0000-000000000001}" />
    <Library name="new_b.js" libraryUniqueId="{e0000000-0000-0000-0000-000000000002}" />
  </formLibraries>
  <events>
    <event name="onload" application="false" active="false">
      <Handlers>
        <Handler functionName="A.load" libraryName="new_a.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000001}" enabled="true" parameters="" passExecutionContext="false" />
        <Handler functionName="A.second" libraryName="new_a.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000002}" enabled="true" parameters="" passExecutionContext="false" />
      </Handlers>
    </event>
    <event name="onchange" application="false" active="false" attribute="name">
      <Handlers>
        <Handler functionName="A.name" libraryName="new_b.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000003}" enabled="true" parameters="" passExecutionContext="false" />
      </Handlers>
    </event>
    <event name="tabstatechange" application="false" active="false" />
  </events>
</form>`;

const WITH_EVENTS = FORM.replace('</form>', EVENTS);

function open(opts: { form?: string; fail?: string } = {}): Call[] {
  const calls = installControls();
  server.use(
    onPath('get', `systemforms(${FORM_ID})`, () => HttpResponse.json({
      systemformid: FORM_ID, name: 'Information', description: '', type: 2, isdefault: true, formactivationstate: 1,
      ismanaged: false, objecttypecode: 'account', formxml: opts.form ?? WITH_EVENTS,
    })),
    onPath('get', 'webresourceset', () => {
      if (opts.fail) return HttpResponse.json({ error: { code: '0x80040220', message: opts.fail } }, { status: 403 });
      return HttpResponse.json({ value: [
        { webresourceid: 'e1', name: 'new_a.js', displayname: 'A', webresourcetype: 3 },
        { webresourceid: 'e2', name: 'new_b.js', displayname: 'B', webresourcetype: 3 },
        { webresourceid: 'e3', name: 'new_c.js', displayname: 'C', webresourcetype: 3 },
      ] });
    }),
  );
  openForm();
  return calls;
}

const events = () => screen.getByRole('region', { name: en['forms.events.title'] });
const list = (name: string) => within(events()).getByRole('group', { name });
const libs = (form: Element) => Array.from(form.getElementsByTagName('Library')).map((l) => l.getAttribute('name'));
const handlers = (form: Element, event: string, attribute?: string) => {
  const el = Array.from(form.getElementsByTagName('event'))
    .find((e) => e.getAttribute('name') === event && (e.getAttribute('attribute') ?? undefined) === attribute);
  return Array.from(el?.getElementsByTagName('Handler') ?? []);
};

describe('EventsEditor', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('lists the libraries and offers the JS web resources not yet on the form', async () => {
    open();
    await canvasReady();
    expect(within(events()).getByRole('button', { name: 'Remove library new_a.js' })).toBeInTheDocument();
    const select = await within(events()).findByRole('combobox', { name: en['forms.events.library.pick'] });
    await userEvent.setup().click(select);
    await waitFor(() => expect(within(select).getByRole('option', { name: 'new_c.js' })).toBeInTheDocument());
    expect(within(select).queryByRole('option', { name: 'new_a.js' })).not.toBeInTheDocument();
  });

  it('adds a library and Save posts it after the others', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const select = await within(events()).findByRole('combobox', { name: en['forms.events.library.pick'] });
    await user.click(select);
    await waitFor(() => expect(within(select).getByRole('option', { name: 'new_c.js' })).toBeInTheDocument());
    await user.selectOptions(select, 'new_c.js');
    await user.click(within(events()).getByRole('button', { name: en['forms.events.library.add'] }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.some((c) => c.path === `systemforms(${FORM_ID})`)).toBe(true));
    const form = savedForm(calls);
    expect(libs(form)).toEqual(['new_a.js', 'new_b.js', 'new_c.js']);
    expect(form.getElementsByTagName('Library')[2].getAttribute('libraryUniqueId')).toMatch(/^\{[0-9a-f-]{36}\}$/);
  });

  it('reorders libraries', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    await user.click(within(events()).getByRole('button', { name: 'Move library new_b.js up' }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(libs(savedForm(calls))).toEqual(['new_b.js', 'new_a.js']);
  });

  it('blocks removing a library a handler uses and names the handler', async () => {
    const user = userEvent.setup();
    open();
    await canvasReady();
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_a.js' }));
    expect(within(events()).getByText(/used by: A\.load, A\.second/)).toBeInTheDocument();
    expect(within(events()).getByRole('button', { name: 'Remove library new_a.js' })).toBeInTheDocument();
  });

  it('removes a library no handler uses', async () => {
    const user = userEvent.setup();
    const calls = open({ form: FORM.replace('</form>', EVENTS.replace('libraryName="new_b.js"', 'libraryName="new_a.js"')) });
    await canvasReady();
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_b.js' }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(libs(savedForm(calls))).toEqual(['new_a.js']);
  });

  it('adds an OnLoad handler, and a blank function name is refused with a message', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const add = within(list(en['forms.events.onload'])).getByRole('group', { name: en['forms.events.handler.new'] });
    await user.click(within(add).getByRole('button', { name: en['forms.events.handler.add'] }));
    expect(within(add).getByText(en['forms.events.functionRequired'])).toBeInTheDocument();
    await user.type(within(add).getByRole('textbox', { name: new RegExp(en['forms.events.function']) }), '  B.init ');
    await user.selectOptions(within(add).getByRole('combobox', { name: en['forms.events.library'] }), 'new_b.js');
    await user.type(within(add).getByRole('textbox', { name: en['forms.events.parameters'] }), '1,"x"');
    await user.click(within(add).getByRole('switch', { name: en['forms.events.passContext'] }));
    await user.click(within(add).getByRole('button', { name: en['forms.events.handler.add'] }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const added = handlers(savedForm(calls), 'onload')[2];
    expect(added.getAttribute('functionName')).toBe('B.init');
    expect(added.getAttribute('libraryName')).toBe('new_b.js');
    expect(added.getAttribute('parameters')).toBe('1,"x"');
    expect(added.getAttribute('passExecutionContext')).toBe('true');
    expect(added.getAttribute('enabled')).toBe('true');
    expect(added.getAttribute('handlerUniqueId')).toMatch(/^\{/);
  });

  it('edits, reorders and removes a handler', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const onload = list(en['forms.events.onload']);
    const first = within(onload).getByRole('group', { name: 'Handler A.load' });
    const name = within(first).getByRole('textbox', { name: new RegExp(en['forms.events.function']) });
    await user.clear(name);
    expect(within(first).getByText(en['forms.events.functionRequired'])).toBeInTheDocument();
    await user.type(name, 'A.start');
    await user.click(within(onload).getByRole('button', { name: 'Move handler A.second up' }));
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.start' }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(handlers(savedForm(calls), 'onload').map((h) => h.getAttribute('functionName'))).toEqual(['A.second']);
  });

  it('a cleared function name shows the message, and on blur reverts to what Save writes', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const first = within(list(en['forms.events.onload'])).getByRole('group', { name: 'Handler A.load' });
    const name = within(first).getByRole('textbox', { name: new RegExp(en['forms.events.function']) });
    await user.clear(name);
    expect(within(first).getByText(en['forms.events.functionRequired'])).toBeInTheDocument();
    await user.tab();
    expect(name).toHaveValue('A.load');
    expect(within(first).queryByText(en['forms.events.functionRequired'])).not.toBeInTheDocument();
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(handlers(savedForm(calls), 'onload')[0].getAttribute('functionName')).toBe('A.load');
  });

  it('a handler whose library is not on the form is locked with a hint until a library is chosen', async () => {
    const user = userEvent.setup();
    const calls = open({ form: FORM.replace('</form>', EVENTS.replace('libraryName="new_b.js"', 'libraryName="gone.js"')) });
    await canvasReady();
    await user.click(within(section('Main')).getByRole('button', { name: 'Account Name' }));
    const changed = within(within(propsCard()).getByRole('group', { name: en['forms.events.onchange'] })).getByRole('group', { name: 'Handler A.name' });
    expect(within(changed).getByText('Library gone.js is not on the form; choose a library first.')).toBeInTheDocument();
    expect(within(changed).getByRole('textbox', { name: new RegExp(en['forms.events.function']) })).toBeDisabled();
    await user.selectOptions(within(changed).getByRole('combobox', { name: en['forms.events.library'] }), 'new_a.js');
    expect(within(changed).getByRole('textbox', { name: new RegExp(en['forms.events.function']) })).toBeEnabled();
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(handlers(savedForm(calls), 'onchange', 'name')[0].getAttribute('libraryName')).toBe('new_a.js');
  });

  it('the blocked message goes when the design changes', async () => {
    const user = userEvent.setup();
    open();
    await canvasReady();
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_a.js' }));
    expect(within(events()).getByText(/cannot be removed/)).toBeInTheDocument();
    await user.click(within(events()).getByRole('button', { name: 'Move library new_b.js up' }));
    expect(within(events()).queryByText(/cannot be removed/)).not.toBeInTheDocument();
  });

  it('focus goes to a neighbour, else the Add button, after a handler or library is removed', async () => {
    const user = userEvent.setup();
    open({ form: FORM.replace('</form>', EVENTS.replace('libraryName="new_b.js"', 'libraryName="new_a.js"')) });
    await canvasReady();
    const onload = list(en['forms.events.onload']);
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.load' }));
    expect(within(onload).getByRole('button', { name: 'Remove handler A.second' })).toHaveFocus();
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.second' }));
    await waitFor(() => expect(within(onload).getByRole('button', { name: en['forms.events.handler.add'] })).toHaveFocus());
    // No handler uses new_b.js here: removing it leaves the focus on the library before it.
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_b.js' }));
    expect(within(events()).getByRole('button', { name: 'Remove library new_a.js' })).toHaveFocus();
  });

  it('removing the last handler of a form without libraries focuses the list (A4)', async () => {
    const user = userEvent.setup();
    open({ form: FORM.replace('</form>', `
  <events>
    <event name="onload" application="false" active="false">
      <Handlers>
        <Handler functionName="A.load" libraryName="new_gone.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000001}" enabled="true" parameters="" passExecutionContext="false" />
      </Handlers>
    </event>
  </events>
</form>`) });
    await canvasReady();
    const onload = list(en['forms.events.onload']);
    expect(within(onload).queryByRole('button', { name: en['forms.events.handler.add'] })).not.toBeInTheDocument();
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.load' }));
    await waitFor(() => expect(list(en['forms.events.onload'])).toHaveFocus());
    expect(within(list(en['forms.events.onload'])).getByText(en['forms.events.handlers.none'])).toBeInTheDocument();
  });

  it('removing the last handler removes the event', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const onload = list(en['forms.events.onload']);
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.load' }));
    await user.click(within(onload).getByRole('button', { name: 'Remove handler A.second' }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const form = savedForm(calls);
    expect(Array.from(form.getElementsByTagName('event')).map((e) => e.getAttribute('name'))).toEqual(['onchange', 'tabstatechange']);
  });

  it('lists the other events read-only, with their names', async () => {
    open();
    await canvasReady();
    expect(within(events()).getByText('Other events not edited here (1): tabstatechange')).toBeInTheDocument();
  });

  it('adds and edits the OnChange handlers of a field in its properties', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    await user.click(within(section('Main')).getByRole('button', { name: 'Account Name' }));
    const onchange = within(propsCard()).getByRole('group', { name: en['forms.events.onchange'] });
    expect(within(onchange).getByRole('group', { name: 'Handler A.name' })).toBeInTheDocument();
    const add = within(onchange).getByRole('group', { name: en['forms.events.handler.new'] });
    await user.type(within(add).getByRole('textbox', { name: new RegExp(en['forms.events.function']) }), 'A.changed');
    await user.click(within(add).getByRole('button', { name: en['forms.events.handler.add'] }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const written = handlers(savedForm(calls), 'onchange', 'name');
    expect(written.map((h) => h.getAttribute('functionName'))).toEqual(['A.name', 'A.changed']);
    expect(written[1].getAttribute('libraryName')).toBe('new_a.js');
  });

  it('adds the first OnChange handler of a field that had no OnChange event', async () => {
    const user = userEvent.setup();
    const calls = open({ form: FORM.replace('</form>', EVENTS.replace(/\s*<event name="onchange"[\s\S]*?<\/event>/, '')) });
    await canvasReady();
    await user.click(within(section('Main')).getByRole('button', { name: 'Account Name' }));
    const onchange = within(propsCard()).getByRole('group', { name: en['forms.events.onchange'] });
    const add = within(onchange).getByRole('group', { name: en['forms.events.handler.new'] });
    await user.type(within(add).getByRole('textbox', { name: new RegExp(en['forms.events.function']) }), 'A.first');
    await user.click(within(add).getByRole('button', { name: en['forms.events.handler.add'] }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const form = savedForm(calls);
    const event = Array.from(form.getElementsByTagName('event')).find((e) => e.getAttribute('name') === 'onchange')!;
    expect(event.parentElement?.tagName).toBe('events');
    expect(event.parentElement?.parentElement).toBe(form);
    expect(Array.from(event.attributes).map((a) => [a.name, a.value]))
      .toEqual([['name', 'onchange'], ['application', 'false'], ['active', 'false'], ['attribute', 'name']]);
    const written = handlers(form, 'onchange', 'name');
    expect(written.map((h) => h.getAttribute('functionName'))).toEqual(['A.first']);
    expect(written[0].parentElement?.tagName).toBe('Handlers');
  });

  it('a quick view form has no Events card and no OnChange handlers', async () => {
    const user = userEvent.setup();
    installControls({}, 6);
    openForm();
    await canvasReady();
    expect(screen.queryByRole('region', { name: en['forms.events.title'] })).not.toBeInTheDocument();
    await user.click(within(section('Main')).getByRole('button', { name: 'Account Name' }));
    expect(within(propsCard()).queryByRole('group', { name: en['forms.events.onchange'] })).not.toBeInTheDocument();
    expect(within(propsCard()).queryByText(en['forms.events.noLibraries'])).not.toBeInTheDocument();
  });

  it('a field without libraries on the form is told to add one first', async () => {
    const user = userEvent.setup();
    open({ form: FORM });
    await canvasReady();
    await user.click(within(section('Main')).getByRole('button', { name: 'Account Name' }));
    expect(within(propsCard()).getByText(en['forms.events.noLibraries'])).toBeInTheDocument();
  });

  it('a failed JS web resource list shows the server text and disables Add', async () => {
    open({ fail: 'Web resources are unavailable' });
    await canvasReady();
    await userEvent.setup().click(await within(events()).findByRole('combobox', { name: en['forms.events.library.pick'] }));
    expect(await within(events()).findByText(/Web resources are unavailable/)).toBeInTheDocument();
    expect(within(events()).getByRole('button', { name: en['forms.events.library.add'] })).toHaveAttribute('aria-disabled', 'true');
  });

  it('a library move that is undone saves the libraries and OnLoad handlers in their first order', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    await user.click(within(events()).getByRole('button', { name: 'Move library new_a.js down' }));
    await user.click(within(events()).getByRole('button', { name: 'Move library new_a.js up' }));
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(libs(savedForm(calls))).toEqual(['new_a.js', 'new_b.js']);
    expect(handlers(savedForm(calls), 'onload').map((h) => h.getAttribute('functionName'))).toEqual(['A.load', 'A.second']);
  });

  it('lists an OnChange of a column not on the form read-only, and Remove drops it (H2)', async () => {
    const user = userEvent.setup();
    const orphan = `
    <event name="onchange" application="false" active="false" attribute="new_gone">
      <Handlers>
        <Handler functionName="A.gone" libraryName="new_a.js" handlerUniqueId="{d0000000-0000-0000-0000-000000000009}" enabled="true" parameters="" passExecutionContext="false" />
      </Handlers>
    </event>
    <event name="tabstatechange"`;
    // new_a.js is used only by the orphan handler.
    const xml = EVENTS.replaceAll('libraryName="new_a.js"', 'libraryName="new_b.js"').replace('\n    <event name="tabstatechange"', orphan);
    const form = FORM.replace('</form>', xml);
    const calls = open({ form });
    await canvasReady();
    const orphans = within(events()).getByRole('group', { name: en['forms.events.orphans'] });
    expect(within(orphans).getByText('new_gone: A.gone')).toBeInTheDocument();
    expect(within(orphans).queryByRole('textbox')).not.toBeInTheDocument();
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_a.js' }));
    expect(within(events()).getByText(/used by: A\.gone/)).toBeInTheDocument();
    await user.click(within(orphans).getByRole('button', { name: 'Remove the OnChange handlers of new_gone' }));
    expect(within(events()).queryByRole('group', { name: en['forms.events.orphans'] })).not.toBeInTheDocument();
    await user.click(within(events()).getByRole('button', { name: 'Remove library new_a.js' }));
    expect(within(events()).queryByRole('button', { name: 'Remove library new_a.js' })).not.toBeInTheDocument();
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const saved = savedForm(calls);
    expect(handlers(saved, 'onchange', 'new_gone')).toEqual([]);
    expect(Array.from(saved.getElementsByTagName('event')).some((e) => e.getAttribute('attribute') === 'new_gone')).toBe(false);
    expect(libs(saved)).toEqual(['new_b.js']);
  });

  it('after Add the new-handler form is back to its defaults (H6)', async () => {
    const user = userEvent.setup();
    const calls = open();
    await canvasReady();
    const add = within(list(en['forms.events.onload'])).getByRole('group', { name: en['forms.events.handler.new'] });
    const name = () => within(add).getByRole('textbox', { name: new RegExp(en['forms.events.function']) });
    const pass = () => within(add).getByRole('switch', { name: en['forms.events.passContext'] });
    const enabled = () => within(add).getByRole('switch', { name: en['forms.events.enabled'] });
    await user.type(name(), 'B.init');
    await user.selectOptions(within(add).getByRole('combobox', { name: en['forms.events.library'] }), 'new_b.js');
    await user.type(within(add).getByRole('textbox', { name: en['forms.events.parameters'] }), '1');
    await user.click(pass());
    await user.click(enabled());
    await user.click(within(add).getByRole('button', { name: en['forms.events.handler.add'] }));
    expect(name()).toHaveValue('');
    expect(within(add).getByRole('textbox', { name: en['forms.events.parameters'] })).toHaveValue('');
    expect(pass()).not.toBeChecked();
    expect(enabled()).toBeChecked();
    expect(within(add).getByRole('combobox', { name: en['forms.events.library'] })).toHaveValue('new_b.js');
    await user.click(saveButton());
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const added = handlers(savedForm(calls), 'onload')[2];
    expect(added.getAttribute('passExecutionContext')).toBe('true');
    expect(added.getAttribute('enabled')).toBe('false');
  });
});

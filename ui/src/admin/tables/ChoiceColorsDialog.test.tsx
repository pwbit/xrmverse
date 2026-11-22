import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { mockApps } from '../../apps/testSupport';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { en } from '../../shell/i18n/en';
import { server } from '../../test/server';
import { decodeContent, encodeContent } from '../views/overlayApi';
import { ChoiceColorsDialog } from './ChoiceColorsDialog';
import { parseAttribute } from './metadata';
import { lbl, rawAttr } from './tableTestSupport';

const ROOT = '/api/data/v9.2';
const DEF = 'dddddddd-0000-0000-0000-000000000001';
const ENV_WR = 'eeeeeeee-0000-0000-0000-000000000001';
const APP_WR = 'eeeeeeee-0000-0000-0000-000000000002';
const KEY = 'xv_project.xv_priority';

const option = (value: number, label: string, color?: string) => ({ Value: value, Label: lbl(label), ...(color ? { Color: color } : {}) });
const local = parseAttribute(
  rawAttr('xv_priority', 'Picklist', { display: 'Priority', OptionSet: { IsGlobal: false, Options: [option(1, 'Low'), option(2, 'High', '#D13438')] } }),
);
const global = parseAttribute(
  rawAttr('xv_level', 'Picklist', { display: 'Level', OptionSet: { IsGlobal: true, Name: 'xv_level', Options: [option(1, 'One'), option(2, 'Two')] } }),
);

interface Seen { method: string; path: string; body?: unknown }

/** The overlays in use (environment, then one app) and their web resources; records every write. `null`: no overlay. */
function install({ environment = 'new_env.json' as string | null, app = 'new_app.json' as string | null, envText = JSON.stringify({ version: 1 }), failUpdate = false as boolean | number, failPublish = 0, hold = undefined as Promise<void> | undefined } = {}) {
  mockApps();
  const seen: Seen[] = [];
  const contents: Record<string, { id: string; text: string }> = {
    'new_env.json': { id: ENV_WR, text: envText },
    'new_app.json': { id: APP_WR, text: JSON.stringify({ version: 1 }) },
  };
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname).slice(ROOT.length + 1);
      if (request.method !== 'GET') {
        const text = await request.text();
        seen.push({ method: request.method, path, body: text ? JSON.parse(text) : undefined });
        const failing = failUpdate === true || (typeof failUpdate === 'number' && (JSON.parse(text) as { Value?: number }).Value === failUpdate);
        if (path === 'UpdateOptionValue' && hold) await hold;
        if (path === 'PublishXml' && failPublish > 0) {
          failPublish--;
          return HttpResponse.json({ error: { code: '0x80040203', message: 'Publish is busy.' } }, { status: 500 });
        }
        if (failing && path === 'UpdateOptionValue')
          return HttpResponse.json({ error: { code: '0x80040203', message: 'Option value 2 does not exist.' } }, { status: 400 });
        return new HttpResponse(null, { status: 204 });
      }
      if (path === 'settingdefinitions') return HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue: null }] });
      if (path === 'organizationsettings') return HttpResponse.json({ value: environment ? [{ value: environment }] : [] });
      if (path === 'appsettings') return HttpResponse.json({ value: app ? [{ _parentappmoduleid_value: 'app1', value: app }] : [] });
      if (path === 'appmodules') return HttpResponse.json({ value: [{ appmoduleid: 'app1', name: 'Sales Hub' }] });
      if (path === 'webresourceset') {
        const name = /name eq '([^']*)'/.exec(url.searchParams.get('$filter') ?? '')?.[1] ?? '';
        const found = contents[name];
        return HttpResponse.json({ value: found ? [{ webresourceid: found.id, name, content: encodeContent(found.text) }] : [] });
      }
      return undefined;
    }),
  );
  return seen;
}

function renderDialog(column = local) {
  const onClose = vi.fn();
  const onSaved = vi.fn();
  render(
    <Providers>
      <ChoiceColorsDialog table="xv_project" column={column} onClose={onClose} onSaved={onSaved} />
    </Providers>,
  );
  return { onClose, onSaved };
}

const group = (name: string) => screen.getByRole('group', { name });
const background = (name: string) => within(group(name)).getByLabelText(en['choiceColors.background']);
const automatic = (name: string) => within(group(name)).getByRole('switch', { name: en['choiceColors.automatic'] });
const pick = (input: HTMLElement, value: string) => fireEvent.change(input, { target: { value } });
const overlayReady = () => waitFor(() => expect(automatic('Low')).toBeEnabled());
const written = (call: Seen) => JSON.parse(decodeContent((call.body as { content: string }).content)) as Record<string, unknown>;
const writes = (seen: Seen[]) => seen.map((s) => `${s.method} ${s.path}`);
const save = (user: ReturnType<typeof userEvent.setup>) => user.click(screen.getByRole('button', { name: en['choiceColors.save'] }));

describe('ChoiceColorsDialog', () => {
  it('lists the options with their colours and an automatic text colour, the environment overlay preselected', async () => {
    install();
    renderDialog();
    await overlayReady();
    expect(screen.getByRole('combobox', { name: en['views.modes.overlay'] })).toHaveValue('new_env.json');
    expect(background('High')).toHaveValue('#d13438');
    expect(within(group('High')).getByRole('button', { name: en['choiceColors.noColour'] })).toBeEnabled();
    expect(within(group('Low')).getByRole('button', { name: en['choiceColors.noColour'] })).toBeDisabled();
    expect(automatic('High')).toBeChecked();
    expect(within(group('High')).queryByLabelText(en['choiceColors.text'])).not.toBeInTheDocument();
    expect(within(group('High')).getByText('High', { selector: '.fui-Badge' })).toHaveStyle({ color: '#FFFFFF' });
    expect(screen.queryByText(en['choiceColors.global'].split('{')[0]!, { exact: false })).not.toBeInTheDocument();
  });

  it('writes a changed background with UpdateOptionValue on the column, then publishes the table; unchanged options are not written', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(seen).toEqual([
      { method: 'POST', path: 'UpdateOptionValue', body: { EntityLogicalName: 'xv_project', AttributeLogicalName: 'xv_priority', Value: 1, Color: '#FF8800', MergeLabels: true } },
      { method: 'POST', path: 'PublishXml', body: { ParameterXml: '<importexportxml><entities><entity>xv_project</entity></entities></importexportxml>' } },
    ]);
  });

  it('publishes once after every background is written', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    pick(background('High'), '#0078d4');
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(writes(seen)).toEqual(['POST UpdateOptionValue', 'POST UpdateOptionValue', 'POST PublishXml']);
  });

  it('after a failed save that wrote some backgrounds, publishes them and reads the table again on close', async () => {
    const seen = install({ failUpdate: 2 });
    const { onSaved, onClose } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    pick(background('High'), '#000000');
    await save(user);
    expect(await screen.findByText((text) => text.includes('Option value 2 does not exist.'))).toBeInTheDocument();
    expect(writes(seen)).toEqual(['POST UpdateOptionValue', 'POST UpdateOptionValue', 'POST PublishXml']);
    expect(onSaved).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: en['common.cancel'] }));
    expect(onSaved).toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('writes no text colour when Automatic is only switched off, or the text picked is the automatic one', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    await user.click(automatic('Low'));
    await user.click(automatic('High'));
    expect(within(group('High')).getByLabelText(en['choiceColors.text'])).toHaveValue('#ffffff');
    pick(within(group('High')).getByLabelText(en['choiceColors.text']), '#ffffff');
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(seen).toEqual([]);
  });

  it('clears a background with "No colour" (Color "")', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    await user.click(within(group('High')).getByRole('button', { name: en['choiceColors.noColour'] }));
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(seen[0]).toEqual({ method: 'POST', path: 'UpdateOptionValue', body: { EntityLogicalName: 'xv_project', AttributeLogicalName: 'xv_priority', Value: 2, Color: '', MergeLabels: true } });
  });

  it('writes a global choice by its name and says the colours apply to every column using it', async () => {
    const seen = install();
    const { onSaved } = renderDialog(global);
    const user = userEvent.setup();
    expect(await screen.findByText(en['choiceColors.global'].replace('{name}', 'xv_level'))).toBeInTheDocument();
    await waitFor(() => expect(automatic('One')).toBeEnabled());
    pick(background('Two'), '#00AA00');
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(seen).toEqual([
      { method: 'POST', path: 'UpdateOptionValue', body: { OptionSetName: 'xv_level', Value: 2, Color: '#00AA00', MergeLabels: true } },
      { method: 'POST', path: 'PublishXml', body: { ParameterXml: '<importexportxml><optionsets><optionset>xv_level</optionset></optionsets></importexportxml>' } },
    ]);
  });

  it('saves a text colour in the chosen overlay under the choice key, without writing the option', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    await user.selectOptions(screen.getByRole('combobox', { name: en['views.modes.overlay'] }), 'new_app.json');
    await overlayReady();
    await user.click(automatic('Low'));
    pick(within(group('Low')).getByLabelText(en['choiceColors.text']), '#123456');
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(writes(seen)).toEqual([`PATCH webresourceset(${APP_WR})`, 'POST PublishXml']);
    expect(written(seen[0]!)).toEqual({ version: 1, choices: { [KEY]: { '1': { text: '#123456' } } } });
  });

  it('removes the text colour when Automatic is switched back on', async () => {
    const seen = install({ envText: JSON.stringify({ version: 1, choices: { [KEY]: { '2': { text: '#111111' } } } }) });
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    expect(automatic('High')).not.toBeChecked();
    expect(within(group('High')).getByLabelText(en['choiceColors.text'])).toHaveValue('#111111');
    await user.click(automatic('High'));
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(writes(seen)).toEqual([`PATCH webresourceset(${ENV_WR})`, 'POST PublishXml']);
    expect(written(seen[0]!)).toEqual({ version: 1 });
  });

  it('disables the text colours with a note when no overlay is in use', async () => {
    install({ environment: null, app: null });
    renderDialog();
    expect(await screen.findByText(en['choiceColors.noOverlay'])).toBeInTheDocument();
    expect(screen.getByRole('link', { name: en['views.modes.environmentLink'] })).toHaveAttribute('href', '/manage/environment');
    expect(automatic('Low')).toBeDisabled();
    expect(screen.queryByRole('combobox', { name: en['views.modes.overlay'] })).not.toBeInTheDocument();
  });

  it('shows the server message when a write fails and stays open', async () => {
    install({ failUpdate: true });
    const { onSaved, onClose } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('High'), '#000000');
    await save(user);
    expect(await screen.findByText((text) => text.startsWith(en['choiceColors.saveFailed'].split('{')[0]!) && text.includes('Option value 2 does not exist.'))).toBeInTheDocument();
    expect(onSaved).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('keeps the first error when the publish after a failed write also fails, and warns about the publish', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const seen = install({ failUpdate: 2, failPublish: 1 });
    renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    pick(background('High'), '#000000');
    await save(user);
    expect(await screen.findByText((text) => text.includes('Option value 2 does not exist.'))).toBeInTheDocument();
    expect(writes(seen)).toEqual(['POST UpdateOptionValue', 'POST UpdateOptionValue', 'POST PublishXml']);
    expect(warn).toHaveBeenCalledTimes(1);
    warn.mockRestore();
  });

  it('after a failed publish, the next Save publishes what was written and closes', async () => {
    const seen = install({ failPublish: 1 });
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    await save(user);
    expect(await screen.findByText((text) => text.includes('Publish is busy.'))).toBeInTheDocument();
    expect(onSaved).not.toHaveBeenCalled();
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(writes(seen)).toEqual(['POST UpdateOptionValue', 'POST PublishXml', 'POST PublishXml']);
  });

  it('cannot be closed (Cancel, Esc) while a save is running', async () => {
    let release!: () => void;
    const hold = new Promise<void>((resolve) => (release = resolve));
    install({ hold });
    const { onSaved, onClose } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    pick(background('Low'), '#ff8800');
    await save(user);
    expect(screen.getByRole('button', { name: en['common.cancel'] })).toBeDisabled();
    await user.keyboard('{Escape}');
    expect(onClose).not.toHaveBeenCalled();
    expect(onSaved).not.toHaveBeenCalled();
    release();
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes without writing when nothing changed', async () => {
    const seen = install();
    const { onSaved } = renderDialog();
    const user = userEvent.setup();
    await overlayReady();
    await save(user);
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(seen).toEqual([]);
  });
});

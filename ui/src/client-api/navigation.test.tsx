import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/server';
import { attr } from '../admin/tables/testAttrs';
import { parseMainAspx } from '../apps/mainAspx';
import { createDialogHost, DialogHostView } from './DialogHost';
import { createNavigation, formParameterValues } from './navigation';

const APP = 'aaaaaaaa-0000-0000-0000-0000000000a1';
const ID = '11111111-2222-3333-4444-555555555555';
const FORM = '22222222-0000-0000-0000-000000000001';

type Fn = (...a: unknown[]) => Promise<unknown>;

function setup() {
  const urls: string[] = [];
  const opened: { url: string; target?: string; features?: string }[] = [];
  const dialogs = createDialogHost();
  const nav = createNavigation({
    appId: APP,
    navigate: (url) => urls.push(url),
    dialogs,
    open: (url, target, features) => (opened.push({ url, target, features }), null),
  }) as unknown as Record<string, Fn>;
  render(<DialogHostView host={dialogs} />);
  return { nav, urls, opened, dialogs };
}

afterEach(() => vi.restoreAllMocks());
// Every web resource exists unless a test says otherwise.
beforeEach(() => server.use(http.get('*/WebResources/*', () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } }))));

/** Runs a call that opens a dialog inside act(): the dialog host re-renders as the call returns. */
function inAct<T>(call: () => T): T {
  let result!: T;
  act(() => {
    result = call();
  });
  return result;
}


describe('Xrm.Navigation', () => {
  it('open_form_navigates_to_record_url_with_form_id', async () => {
    const { nav, urls, opened } = setup();
    await nav.openForm!({ entityName: 'account', entityId: `{${ID.toUpperCase()}}`, formId: FORM });
    expect(urls).toHaveLength(1);
    expect(parseMainAspx(new URL(urls[0]!, 'http://x').search)).toEqual({ appId: APP, pageType: 'entityrecord', etn: 'account', id: ID, formId: FORM });
    // In a new window: the same address, not through the router.
    await nav.openForm!({ entityName: 'account', entityId: ID, openInNewWindow: true });
    expect(urls).toHaveLength(1);
    expect(opened[0]!.url).toContain(`etn=account&id=${ID}`);
    expect(opened[0]!.target).toBe('_blank');
    await expect(nav.openForm!({})).rejects.toThrow('entityName');
    await expect(nav.openForm!({ entityName: 'account', useQuickCreateForm: true })).rejects.toThrow('not available in this version');
    await expect(nav.openForm!({ entityName: 'account', createFromEntity: { entityType: 'account', id: ID } })).rejects.toThrow('not available in this version');
  });

  it('open_form_create_with_parameters', async () => {
    const { nav, urls } = setup();
    await nav.openForm!({ entityName: 'op365_project' }, { op365_name: 'From script', op365_budget: 12.5, op365_parentid: ID, op365_parentidname: 'Parent', op365_parentidtype: 'op365_project' });
    const state = parseMainAspx(new URL(urls[0]!, 'http://x').search);
    expect(state.id).toBeUndefined();
    expect(state.extraqs).toEqual({ op365_name: 'From script', op365_budget: '12.5', op365_parentid: ID, op365_parentidname: 'Parent', op365_parentidtype: 'op365_project' });

    // The create form reads them as extraqs: each is converted to its column; one the form does not show is reported.
    const columns = [
      attr('op365_name'),
      attr('op365_budget', { kind: 'decimal' }),
      attr('op365_active', { kind: 'bool' }),
      attr('op365_parentid', { kind: 'lookup', targets: ['op365_project'] }),
    ];
    const result = formParameterValues(columns, ['op365_name', 'op365_budget', 'op365_parentid', 'op365_active'], { ...state.extraqs, op365_active: '1', other: 'x', op365_budget2: '' }, () => 'op365_parentid');
    // Parameter names in any case, the lookup's name and type included.
    const mixed = formParameterValues(columns, ['op365_parentid'], { OP365_ParentId: ID, op365_ParentIdName: 'Parent', OP365_PARENTIDTYPE: 'op365_project' }, () => 'op365_parentid');
    expect(mixed).toEqual({ values: { op365_parentid: { id: ID, name: 'Parent', target: 'op365_project', nav: 'op365_parentid' } }, ignored: [] });
    expect(result.values).toEqual({
      op365_name: 'From script',
      op365_budget: '12.5',
      op365_active: true,
      op365_parentid: { id: ID, name: 'Parent', target: 'op365_project', nav: 'op365_parentid' },
    });
    expect(result.ignored).toEqual(['other', 'op365_budget2']);
  });

  it('navigate_to_entitylist_inline', async () => {
    const { nav, urls } = setup();
    await expect(nav.navigateTo!({ pageType: 'entitylist', entityName: 'op365_goodsreceipt' })).resolves.toBeUndefined();
    expect(parseMainAspx(new URL(urls[0]!, 'http://x').search)).toEqual({ appId: APP, pageType: 'entitylist', etn: 'op365_goodsreceipt' });
    await nav.navigateTo!({ pageType: 'entitylist', entityName: 'account', viewId: `{${FORM}}`, viewType: 'userquery' }, { target: 1 });
    expect(parseMainAspx(new URL(urls[1]!, 'http://x').search)).toMatchObject({ viewId: FORM, viewType: 4230 });
    await nav.navigateTo!({ pageType: 'entityrecord', entityName: 'account', entityId: ID });
    expect(urls[2]).toContain(`pagetype=entityrecord&etn=account&id=${ID}`);
    await nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/decision.html', data: 'a=1&b=2' });
    expect(parseMainAspx(new URL(urls[3]!, 'http://x').search)).toMatchObject({ pageType: 'webresource', name: 'op365_/pages/decision.html', data: 'a=1&b=2' });
    // Learn: a list opens inline only; dashboards and custom pages are not in this version.
    await expect(nav.navigateTo!({ pageType: 'entitylist', entityName: 'account' }, { target: 2 })).rejects.toThrow('inline');
    await expect(nav.navigateTo!({ pageType: 'dashboard' })).rejects.toThrow('not available in this version');
    await expect(nav.navigateTo!({ pageType: 'custom', name: 'x' })).rejects.toThrow('not available in this version');
    await expect(nav.navigateTo!({ pageType: 'webresource', webresourceName: 'javascript:alert(1)' })).rejects.toThrow('not a web resource name');
    expect(urls).toHaveLength(4);
  });

  it('a_record_dialog_passes_the_form_url_and_size_and_resolves_with_the_saved_record', async () => {
    const dialogs = createDialogHost();
    const nav = createNavigation({ appId: APP, navigate: () => {}, dialogs, open: () => null }) as unknown as Record<string, Fn>;
    const seen: unknown[] = [];
    render(
      <DialogHostView
        host={dialogs}
        renderRecord={(options, done) => {
          seen.push(options);
          return <button onClick={() => done({ entityType: 'account', id: ID, name: 'Contoso' })}>Saved</button>;
        }}
      />,
    );
    const created = inAct(() => nav.navigateTo!({ pageType: 'entityrecord', entityName: 'account', formId: FORM }, { target: 2, position: 2, width: 40, title: 'Quick' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Saved' }));
    expect(await created).toEqual({ savedEntityReference: [{ entityType: 'account', id: ID, name: 'Contoso' }] });
    expect(seen[0]).toMatchObject({ title: 'Quick', position: 2, width: 40 });
    expect(parseMainAspx(new URL((seen[0] as { url: string }).url, 'http://x').search)).toEqual({ appId: APP, pageType: 'entityrecord', etn: 'account', formId: FORM });
    // An existing record's dialog resolves with nothing (Learn: savedEntityReference is for the create form).
    const edited = inAct(() => nav.navigateTo!({ pageType: 'entityrecord', entityName: 'account', entityId: ID }, { target: 2 }));
    await userEvent.click(await screen.findByRole('button', { name: 'Saved' }));
    expect(await edited).toBeUndefined();
  });

  it('a_record_dialog_without_a_form_renderer_resolves_at_once', async () => {
    const { nav } = setup();
    expect(await inAct(() => nav.navigateTo!({ pageType: 'entityrecord', entityName: 'account' }, { target: 2 }))).toEqual({ savedEntityReference: [] });
  });

  it('dialog_window_close_closes_and_resolves', async () => {
    const { nav } = setup();
    let done = false;
    const closed = inAct(() => nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/decision.html' }, { target: 2, title: 'Decide' })).then((v) => {
      done = true;
      return v;
    });
    const dialog = await screen.findByRole('dialog', { name: 'Decide' });
    const frame = await waitFor(() => {
      const f = dialog.querySelector('iframe');
      expect(f).not.toBeNull();
      return f!;
    });
    // Ruling C3: once the page has loaded, the shell has replaced close() on its window.
    act(() => void frame.dispatchEvent(new Event('load')));
    expect(done).toBe(false);
    act(() => frame.contentWindow!.close());
    expect(await closed).toBeUndefined();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('dialog_window_close_is_installed_before_the_page_has_loaded', async () => {
    const { nav } = setup();
    const closed = inAct(() => nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/decision.html' }, { target: 2, title: 'Decide' }));
    const dialog = await screen.findByRole('dialog', { name: 'Decide' });
    const frame = await waitFor(() => {
      const f = dialog.querySelector('iframe');
      expect(f).not.toBeNull();
      return f!;
    });
    // A page that loaded has a new window object with the native close: the shell replaces it without waiting for `load`.
    const native = () => undefined;
    // The test cannot load a real new document, so it replaces `close` on the same window and fires the `pagehide` a real
    // navigation fires. Without it the outcome depends on timing: on a slow runner the poll has already installed the
    // override and stopped before the swap, and only `pagehide` restarts it.
    Object.defineProperty(frame.contentWindow!, 'close', { value: native, configurable: true, writable: true });
    frame.contentWindow!.dispatchEvent(new Event('pagehide'));
    await waitFor(() => expect(frame.contentWindow!.close).not.toBe(native));
    act(() => frame.contentWindow!.close());
    await closed;
  });

  it('dialog_close_poll_stops_after_install_and_restarts_on_pagehide', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    try {
      const { nav } = setup();
      const closed = inAct(() => nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/decision.html' }, { target: 2, title: 'Decide' }));
      const dialog = await screen.findByRole('dialog', { name: 'Decide' });
      const frame = await screen.findByTitle('Web resource op365_/pages/decision.html').then((f) => f as HTMLIFrameElement);
      expect(dialog.contains(frame)).toBe(true);
      const win = frame.contentWindow!;
      const native = () => undefined;
      const fresh = () => Object.defineProperty(win, 'close', { value: native, configurable: true, writable: true });
      fresh();
      const running = vi.getTimerCount();
      expect(running).toBeGreaterThan(0);
      // The first tick installs the override, and the poll is gone.
      act(() => void vi.advanceTimersByTime(10));
      expect(win.close).not.toBe(native);
      expect(vi.getTimerCount()).toBe(running - 1);
      act(() => void vi.advanceTimersByTime(10_000));
      expect(vi.getTimerCount()).toBe(running - 1);
      // The page goes away: the poll starts for the next document, installs, and stops again.
      fresh();
      act(() => void win.dispatchEvent(new Event('pagehide')));
      expect(vi.getTimerCount()).toBe(running);
      act(() => void vi.advanceTimersByTime(10));
      expect(win.close).not.toBe(native);
      expect(vi.getTimerCount()).toBe(running - 1);
      act(() => win.close());
      await closed;
    } finally {
      vi.useRealTimers();
    }
  });

  it('dialog_close_poll_gives_up_after_about_two_seconds', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    try {
      const { nav } = setup();
      const before = vi.getTimerCount();
      inAct(() => nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/decision.html' }, { target: 2, title: 'Decide' }));
      const frame = (await screen.findByTitle('Web resource op365_/pages/decision.html')) as HTMLIFrameElement;
      // A window the shell cannot change: the install never succeeds.
      Object.defineProperty(frame.contentWindow!, 'close', { value: () => undefined, configurable: false, writable: false });
      // The poll starts in an effect after the frame renders: on a slow runner it may not be running yet when the frame is found.
      await vi.waitFor(() => expect(vi.getTimerCount()).toBeGreaterThan(before));
      const running = vi.getTimerCount();
      act(() => void vi.advanceTimersByTime(1_900));
      expect(vi.getTimerCount()).toBe(running);
      act(() => void vi.advanceTimersByTime(300));
      expect(vi.getTimerCount()).toBe(running - 1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('dialog_missing_webresource_shows_the_message', async () => {
    server.use(http.get('*/WebResources/op365_/pages/gone.html', () => new HttpResponse(null, { status: 404 })));
    const { nav } = setup();
    inAct(() => nav.navigateTo!({ pageType: 'webresource', webresourceName: 'op365_/pages/gone.html' }, { target: 2, title: 'Gone' }));
    const dialog = await screen.findByRole('dialog', { name: 'Gone' });
    expect(await within(dialog).findByText('The web resource op365_/pages/gone.html does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(dialog.querySelector('iframe')).toBeNull();
  });

  it.each(['../admin', '%2e%2e/admin', '/admin', 'a\\b'])('dialog_host_refuses_webresource_name_%s', async (name) => {
    const { dialogs } = setup();
    await expect(dialogs.webResource({ name })).rejects.toThrow('is not a web resource name');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('navigate_to_webresource_dialog_resolves_on_close', async () => {
    const { nav, urls } = setup();
    let done = false;
    const closed = inAct(() =>
      nav.navigateTo!(
        { pageType: 'webresource', webresourceName: 'op365_/pages/decision.html', data: 'action=x&id=1' },
        { target: 2, position: 1, width: { value: 480, unit: 'px' }, height: 320, title: 'Reject' },
      ),
    ).then((v) => {
      done = true;
      return v;
    });
    const dialog = await screen.findByRole('dialog', { name: 'Reject' });
    const frame = await waitFor(() => {
      const f = dialog.querySelector('iframe');
      expect(f).not.toBeNull();
      return f!;
    });
    expect(frame.getAttribute('src')).toBe('/WebResources/op365_/pages/decision.html?data=action%3Dx%26id%3D1');
    expect(dialog.style.width).toBe('480px');
    expect(dialog.style.height).toBe('320px');
    expect(done).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(await closed).toBeUndefined();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(urls).toEqual([]);
  });

  it('alert_confirm_error_dialogs_resolve', async () => {
    const { nav } = setup();
    const alerted = inAct(() => nav.openAlertDialog!({ text: 'Save first.', title: 'Note', confirmButtonLabel: 'Understood' }));
    expect(await screen.findByRole('dialog', { name: 'Note' })).toHaveTextContent('Save first.');
    await userEvent.click(screen.getByRole('button', { name: 'Understood' }));
    await expect(alerted).resolves.toBeUndefined();

    const confirmed = inAct(() => nav.openConfirmDialog!({ title: 'Save Baseline', text: 'Save it?', confirmButtonLabel: 'Save Baseline' }));
    await screen.findByRole('dialog', { name: 'Save Baseline' });
    await userEvent.click(screen.getByRole('button', { name: 'Save Baseline' }));
    await expect(confirmed).resolves.toEqual({ confirmed: true });

    const cancelled = inAct(() => nav.openConfirmDialog!({ text: 'Delete?' }));
    await screen.findByText('Delete?');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await expect(cancelled).resolves.toEqual({ confirmed: false });

    const errored = inAct(() => nav.openErrorDialog!({ message: 'The baseline was not saved.', details: 'Stack', errorCode: 0x80040265 }));
    const error = await screen.findByRole('dialog', { name: 'Error' });
    expect(error).toHaveTextContent('The baseline was not saved.');
    expect(error).toHaveTextContent('0x80040265');
    expect(error).toHaveTextContent('Stack');
    await userEvent.click(screen.getByRole('button', { name: 'OK' }));
    await expect(errored).resolves.toBeUndefined();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('open_url_refuses_javascript_scheme', () => {
    const { nav, opened } = setup();
    const openUrl = nav.openUrl as unknown as (url: unknown, o?: unknown) => void;
    for (const bad of ['javascript:alert(1)', ' JavaScript:alert(1)', 'data:text/html,<script>x</script>', 'vbscript:x', 'file:///etc/passwd']) {
      expect(() => openUrl(bad), bad).toThrow('only http and https');
    }
    expect(opened).toEqual([]);
    openUrl('https://example.com/a?b=1', { width: 400, height: 300 });
    openUrl('/relative/page');
    expect(opened[0]).toEqual({ url: 'https://example.com/a?b=1', target: '_blank', features: 'noopener,noreferrer,width=400,height=300' });
    expect(opened[1]!.url).toBe(`${location.origin}/relative/page`);

    const openWebResource = nav.openWebResource as unknown as (name: string, o?: unknown, data?: string) => void;
    openWebResource('op365_/pages/receipt.html', { width: 600 }, 'id=1');
    expect(opened[2]).toEqual({ url: '/WebResources/op365_/pages/receipt.html?data=id%3D1', target: '_blank', features: 'width=600' });
    expect(() => openWebResource('javascript:alert(1)')).toThrow('not a web resource name');
    // Learn: other parameters come appended to the name; the query is not part of the name.
    openWebResource('op365_/pages/receipt.html?typename=account', undefined, 'id=1');
    expect(opened[3]!.url).toBe('/WebResources/op365_/pages/receipt.html?typename=account&data=id%3D1');
    expect(() => (nav.openFile as unknown as () => void)()).toThrow('Xrm.Navigation.openFile is not available in this version');
  });

  it('dialogs_stay_usable_when_several_open', async () => {
    const { dialogs } = setup();
    const first = inAct(() => dialogs.alert({ text: 'One' }));
    const second = inAct(() => dialogs.alert({ text: 'Two' }));
    await screen.findByText('Two');
    await userEvent.click(screen.getAllByRole('button', { name: 'OK' }).at(-1)!);
    await expect(second).resolves.toBeUndefined();
    await userEvent.click(screen.getByRole('button', { name: 'OK' }));
    await expect(first).resolves.toBeUndefined();
  });
});

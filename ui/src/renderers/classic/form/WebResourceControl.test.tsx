import { act, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { attr } from '../../../admin/tables/testAttrs';
import type { Control } from '../../../client-api/control';
import { createUiOverlay } from '../../../client-api/uiOverlay';
import { mountForm } from '../../../client-api/testForm';
import { XrmHostContext, type XrmHost } from '../../../client-api/xrm';
import type { Cell, WebResourceControl as WebResourceModel } from '../../../model/form';
import { setUiLanguage } from '../../../shell/i18n/t';
import { server } from '../../../test/server';
import type { ControlEnv } from './controlEnv';
import { contextOf, field, labelsOf, RECORD_ID, simpleForm } from './formTestSupport';
import { WebResourceControl } from './WebResourceControl';

afterEach(() => setUiLanguage(undefined));
// Every web resource exists unless a test says otherwise.
beforeEach(() => server.use(http.get('*/WebResources/*', () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } }))));

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };

const model = (over: Partial<WebResourceModel> = {}): WebResourceModel => ({
  kind: 'webresource',
  id: 'w',
  classId: '{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}',
  iframe: false,
  url: 'op365_/pages/summary.html',
  data: '',
  passParameters: false,
  restrictScripting: false,
  scrolling: 'auto',
  border: false,
  ...over,
});

const cellOf = (control: WebResourceModel, over: Partial<Cell> = {}): Cell => ({ ...field('x'), id: 'w', control, labels: labelsOf('Page'), ...over });

const host = (org: { uniqueName: string; languageId: number }): XrmHost =>
  ({ xrm: {}, globalContext: { organizationSettings: org }, install: () => {}, setCurrentForm: () => {}, currentForm: () => null }) as unknown as XrmHost;

function renderControl(control: WebResourceModel, options: { lcid?: number; record?: boolean; org?: { uniqueName: string; languageId: number } } = {}) {
  const cell = cellOf(control);
  const ctx = contextOf([name], options.record === false ? undefined : RECORD);
  const env = { ctx, lcid: options.lcid ?? 1033, overlay: createUiOverlay() } as unknown as ControlEnv;
  const view = render(
    <XrmHostContext.Provider value={options.org ? host(options.org) : null}>
      <WebResourceControl cell={cell} control={control} env={env} />
    </XrmHostContext.Provider>,
  );
  return { ...view, frame: () => view.container.querySelector('iframe'), ready: () => screen.findByTitle(/^Web resource /) };
}

describe('WebResourceControl', () => {
  it('webresource_control_src_with_data', async () => {
    const { frame, ready } = renderControl(model({ data: 'a=1&b=2' }));
    await ready();
    expect(frame()!.getAttribute('src')).toBe('/WebResources/op365_/pages/summary.html?data=a%3D1%26b%3D2');
    // Same origin: no sandbox.
    expect(frame()!.hasAttribute('sandbox')).toBe(false);
    expect(frame()).toHaveAccessibleName('Web resource op365_/pages/summary.html');
  });

  it('pass_parameters_appends_id_typename_orgname_lcids', async () => {
    const { frame, ready } = renderControl(model({ passParameters: true, data: 'x' }), { lcid: 1040, org: { uniqueName: 'xrmverse', languageId: 1033 } });
    await ready();
    expect(frame()!.getAttribute('src')).toBe(
      `/WebResources/op365_/pages/summary.html?data=x&id=%7B${RECORD_ID.toUpperCase()}%7D&orglcid=1033&orgname=xrmverse&typename=xv_project&userlcid=1040`,
    );
  });

  it('pass_parameters_id_is_empty_on_the_create_form', async () => {
    const { frame, ready } = renderControl(model({ passParameters: true }), { record: false, org: { uniqueName: 'xrmverse', languageId: 1033 } });
    await ready();
    expect(frame()!.getAttribute('src')).toContain('?id=&orglcid=1033&orgname=xrmverse&typename=xv_project');
  });

  it('iframe_passes_parameters_after_its_own_query', () => {
    const { frame } = renderControl(model({ iframe: true, url: '/pages/x.htm?a=1', passParameters: true, data: 'ignored' }), { org: { uniqueName: 'o', languageId: 1033 } });
    expect(frame()!.getAttribute('src')).toMatch(/^\/pages\/x\.htm\?a=1&id=%7B/);
    expect(frame()!.getAttribute('src')).not.toContain('data=');
  });

  it('iframe_external_url_sandboxed_no_parent_xrm', () => {
    const { frame } = renderControl(model({ iframe: true, url: 'https://example.org/page?x=1' }));
    expect(frame()!.getAttribute('src')).toBe('https://example.org/page?x=1');
    const tokens = frame()!.getAttribute('sandbox')!.split(' ').sort();
    // Scripts, forms and pop-ups only: without allow-same-origin the frame has an opaque origin and cannot reach parent.Xrm.
    expect(tokens).toEqual(['allow-forms', 'allow-popups', 'allow-scripts']);
  });

  it('restricted_cross_frame_scripting_sandboxes_every_address', () => {
    const { frame } = renderControl(model({ iframe: true, url: '/pages/x.htm', restrictScripting: true }));
    expect(frame()!.getAttribute('sandbox')).toBe('');
  });

  it.each(['javascript:alert(1)', 'data:text/html,<b>x</b>', 'vbscript:x', 'file:///etc/passwd'])('iframe_refuses_%s', (url) => {
    const { frame } = renderControl(model({ iframe: true, url }));
    expect(frame()).toBeNull();
    expect(screen.getByText(`The address ${url} cannot be shown: only http and https addresses are allowed.`)).toBeInTheDocument();
  });

  it('a_control_without_a_resource_says_so', () => {
    const { frame } = renderControl(model({ url: '' }));
    expect(frame()).toBeNull();
    expect(screen.getByText('This control has no web resource or address set.')).toBeInTheDocument();
  });

  it('parent_xrm_reachable_from_same_origin_frame', async () => {
    // What the app shell does while an app is open.
    const xrm = { marker: 'shell' };
    (window as unknown as { Xrm?: unknown }).Xrm = xrm;
    try {
      const { frame, ready } = renderControl(model());
      await ready();
      const inner = frame()!.contentWindow!;
      expect(frame()!.hasAttribute('sandbox')).toBe(false);
      // The frame reaches the shell's window through `parent` (jsdom's `parent` object hides the shell's globals, so the window
      // is read through its document, which is the same window).
      expect(inner.parent.document).toBe(document);
      expect((inner.parent.document.defaultView as unknown as { Xrm?: unknown }).Xrm).toBe(xrm);
    } finally {
      delete (window as unknown as { Xrm?: unknown }).Xrm;
    }
  });

  it('a_missing_web_resource_shows_the_message_and_no_frame', async () => {
    server.use(http.get('*/WebResources/op365_/pages/summary.html', () => new HttpResponse(null, { status: 404 })));
    const { frame } = renderControl(model());
    expect(await screen.findByText('The web resource op365_/pages/summary.html does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(frame()).toBeNull();
  });

  it('a_server_failure_is_not_reported_as_missing', async () => {
    server.use(http.get('*/WebResources/op365_/pages/summary.html', () => new HttpResponse(null, { status: 500, statusText: 'Boom' })));
    renderControl(model());
    expect(await screen.findByText(/500 Boom/)).toBeInTheDocument();
  });

  it('an_external_address_is_not_checked', () => {
    const asked: string[] = [];
    server.use(http.get('https://example.org/*', ({ request }) => (asked.push(request.url), new HttpResponse(null, { status: 404 }))));
    const { frame } = renderControl(model({ iframe: true, url: 'https://example.org/page' }));
    expect(frame()).not.toBeNull();
    expect(asked).toEqual([]);
  });

  it.each(['../admin', '%2e%2e/admin', 'a/%2E%2E/admin', '/admin', '\\admin', 'a\\..\\b', 'a/%252e%252e/b'])('webresource_name_%s_is_refused_without_a_request', (url) => {
    const asked: string[] = [];
    server.use(http.get('*/*', ({ request }) => void asked.push(request.url)));
    const { frame } = renderControl(model({ url }));
    expect(frame()).toBeNull();
    expect(screen.getByText(`${url} is not a web resource name.`)).toBeInTheDocument();
    expect(asked).toEqual([]);
  });

  it('italian_title_and_message', () => {
    setUiLanguage(1040);
    renderControl(model({ iframe: true, url: 'javascript:x' }));
    expect(screen.getByText("L'indirizzo javascript:x non può essere mostrato: sono ammessi solo indirizzi http e https.")).toBeInTheDocument();
  });
});

describe('web resource control in formContext', () => {
  const mount = (control: WebResourceModel = model()) => mountForm(simpleForm([cellOf(control)]), contextOf([name], RECORD));
  type Ctl = Control & Record<string, (...a: unknown[]) => unknown>;

  it('get_object_returns_content_window', async () => {
    const m = mount();
    await screen.findByTitle('Web resource op365_/pages/summary.html');
    const c = m.fc().getControl('w') as Ctl;
    expect(c.getControlType()).toBe('webresource');
    // Learn: getObject is the IFRAME element; its window is the content window.
    const el = c.getObject() as HTMLIFrameElement;
    expect(el).toBe(screen.getByTitle('Web resource op365_/pages/summary.html'));
    expect(el.contentWindow).not.toBeNull();
    const pending = c.getContentWindow() as Promise<Window>;
    act(() => void el.dispatchEvent(new Event('load')));
    expect(await pending).toBe(el.contentWindow);
  });

  it('iframe_control_type_is_iframe', async () => {
    const m = mount(model({ iframe: true, url: 'https://example.org/' }));
    await screen.findByTitle('Web resource https://example.org/');
    expect((m.fc().getControl('w') as Ctl).getControlType()).toBe('iframe');
  });

  it('set_src_reloads', async () => {
    const m = mount(model({ data: 'a=1' }));
    const first = (await screen.findByTitle('Web resource op365_/pages/summary.html')) as HTMLIFrameElement;
    const c = m.fc().getControl('w') as Ctl;
    expect(c.getSrc()).toBe('/WebResources/op365_/pages/summary.html?data=a%3D1');
    expect(c.getInitialUrl()).toBe(c.getSrc());
    act(() => void c.setSrc('/WebResources/op365_/pages/other.html?x=2'));
    await waitFor(() => expect(screen.getByTitle('Web resource op365_/pages/summary.html').getAttribute('src')).toBe('/WebResources/op365_/pages/other.html?x=2'));
    // A new address is a new frame, so the page loads again.
    expect(screen.getByTitle('Web resource op365_/pages/summary.html')).not.toBe(first);
    expect(c.getSrc()).toBe('/WebResources/op365_/pages/other.html?x=2');
    expect(c.getObject()).toBe(screen.getByTitle('Web resource op365_/pages/summary.html'));
    // An external address is sandboxed from the moment it is set.
    act(() => void c.setSrc('https://example.org/'));
    await waitFor(() => expect(screen.getByTitle('Web resource op365_/pages/summary.html').getAttribute('sandbox')).toBe('allow-scripts allow-forms allow-popups'));
  });

  it('get_content_window_settles_for_a_sandboxed_frame_that_has_already_loaded', async () => {
    const m = mount(model({ iframe: true, url: 'https://example.org/page' }));
    const el = (await screen.findByTitle('Web resource https://example.org/page')) as HTMLIFrameElement;
    act(() => void el.dispatchEvent(new Event('load')));
    // The load event is past and the document is not readable from the shell (another origin): the window is returned.
    Object.defineProperty(el, 'contentDocument', { value: null });
    const win = await Promise.race([(m.fc().getControl('w') as Ctl).getContentWindow() as Promise<Window>, new Promise((r) => setTimeout(() => r('hung'), 500))]);
    expect(win).toBe(el.contentWindow);
  });

  it('get_content_window_settles_with_the_new_frame_when_set_src_replaces_it_before_load', async () => {
    const m = mount();
    const first = (await screen.findByTitle('Web resource op365_/pages/summary.html')) as HTMLIFrameElement;
    const c = m.fc().getControl('w') as Ctl;
    const pending = c.getContentWindow() as Promise<Window>;
    act(() => void c.setSrc('/WebResources/op365_/pages/other.html'));
    await waitFor(() => expect(screen.getByTitle('Web resource op365_/pages/summary.html')).not.toBe(first));
    const second = screen.getByTitle('Web resource op365_/pages/summary.html') as HTMLIFrameElement;
    act(() => void second.dispatchEvent(new Event('load')));
    expect(await Promise.race([pending, new Promise((r) => setTimeout(() => r('hung'), 1000))])).toBe(second.contentWindow);
  });

  it('set_src_refuses_a_scheme_that_is_not_http', async () => {
    const m = mount();
    await screen.findByTitle('Web resource op365_/pages/summary.html');
    const c = m.fc().getControl('w') as Ctl;
    expect(() => c.setSrc('javascript:alert(1)')).toThrow('formContext.getControl("w").setSrc: javascript:alert(1) is not allowed');
    expect(() => c.setSrc(5)).toThrow('the address must be a string');
    expect(c.getSrc()).toBe('/WebResources/op365_/pages/summary.html');
  });
});

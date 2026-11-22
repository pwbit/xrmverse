import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createSidePanes } from '../client-api/xrmverse/sidePanes';
import { server } from '../test/server';
import { SidePaneArea } from './SidePaneArea';

const ok = (name: string) => server.use(http.get(`*/WebResources/${name}`, () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } })));
const setup = () => {
  const store = createSidePanes();
  const utils = render(<SidePaneArea store={store} />);
  return { store, ...utils };
};
const mockNarrow = (narrow: boolean) =>
  vi.stubGlobal('matchMedia', (q: string) => ({ matches: narrow, media: q, addEventListener() {}, removeEventListener() {} }));

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SidePaneArea', () => {
  it('draws_nothing_without_panes', () => {
    const { container } = setup();
    expect(container).toBeEmptyDOMElement();
  });

  it('strip_shows_one_icon_per_visible_pane_with_title_and_badge', async () => {
    const { store } = setup();
    await act(async () => {
      const a = await store.api.createPane({ title: 'Chat', isSelected: false });
      await store.api.createPane({ title: 'Secret', hidden: true, isSelected: false });
      a.badge = 3;
    });
    expect(screen.getByRole('button', { name: 'Chat' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Secret' })).toBeNull();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('collapse_sets_state_and_hides_content', async () => {
    ok('op_/a.html');
    const { store } = setup();
    await act(async () => {
      const p = await store.api.createPane({ title: 'Chat' });
      void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
    });
    expect(await screen.findByTitle('Web resource op_/a.html')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Collapse side panes' }));
    expect(store.api.state).toBe(0);
    expect(screen.queryByTitle('Web resource op_/a.html')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Expand side panes' }));
    expect(store.api.state).toBe(1);
    expect(await screen.findByTitle('Web resource op_/a.html')).toBeVisible();
  });

  it('collapse_remounts_a_normal_pane_but_keeps_an_alwaysRender_node', async () => {
    ok('op_/a.html');
    ok('op_/b.html');
    const { store } = setup();
    await act(async () => {
      const a = await store.api.createPane({ title: 'A', alwaysRender: true, isSelected: false });
      void a.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
      const b = await store.api.createPane({ title: 'B' });
      void b.navigate({ pageType: 'webresource', webresourceName: 'op_/b.html' });
    });
    const frameA = await screen.findByTitle('Web resource op_/a.html');
    const frameB = await screen.findByTitle('Web resource op_/b.html');
    await userEvent.click(screen.getByRole('button', { name: 'Collapse side panes' }));
    expect(screen.getByTitle('Web resource op_/a.html')).toBe(frameA);
    expect(screen.queryByTitle('Web resource op_/b.html')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Expand side panes' }));
    expect(await screen.findByTitle('Web resource op_/b.html')).not.toBe(frameB);
    expect(screen.getByTitle('Web resource op_/a.html')).toBe(frameA);
  });

  it('hidden_pane_mounts_nothing_until_unhidden_even_with_alwaysRender', async () => {
    ok('op_/a.html');
    const { store } = setup();
    let p!: Awaited<ReturnType<typeof store.api.createPane>>;
    await act(async () => {
      p = await store.api.createPane({ title: 'A', alwaysRender: true, hidden: true, isSelected: false });
      void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
      await store.api.createPane({ title: 'Other', isSelected: false });
    });
    expect(screen.queryByTitle('Web resource op_/a.html')).toBeNull();
    await act(async () => {
      p.hidden = false;
    });
    expect(await screen.findByTitle('Web resource op_/a.html')).toBeInTheDocument();
  });

  it('tab_without_imageSrc_shows_a_generic_icon', async () => {
    const { store } = setup();
    await act(async () => {
      await store.api.createPane({ title: 'Chat', isSelected: false });
    });
    const tab = screen.getByRole('button', { name: 'Chat' });
    expect(tab.querySelector('svg')).not.toBeNull();
    expect(tab).not.toHaveTextContent('C');
  });

  it('header_close_button_follows_canClose_and_hideHeader', async () => {
    const { store } = setup();
    await act(async () => {
      await store.api.createPane({ title: 'Fixed', canClose: false, isSelected: false });
    });
    await userEvent.click(screen.getByRole('button', { name: 'Fixed' }));
    expect(screen.getByText('Fixed', { selector: 'span' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Close pane' })).toBeNull();
    await act(async () => {
      await store.api.createPane({ title: 'Closable' });
    });
    await userEvent.click(screen.getByRole('button', { name: 'Close pane' }));
    expect(store.api.getAllPanes().map((p) => p.title)).toEqual(['Fixed']);
    await act(async () => {
      const p = await store.api.createPane({ title: 'Bare', hideHeader: true });
      expect(p.hideHeader).toBe(true);
    });
    expect(screen.queryByRole('button', { name: 'Close pane' })).toBeNull();
  });

  it('iframe_src_and_loaded_resolves_navigate', async () => {
    ok('op_/a.html');
    const { store } = setup();
    let done = false;
    await act(async () => {
      const p = await store.api.createPane({ title: 'Chat' });
      void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html', data: 'x=1' }).then(() => (done = true));
    });
    const frame = await screen.findByTitle('Web resource op_/a.html');
    expect(frame.getAttribute('src')).toBe('/WebResources/op_/a.html?data=x%3D1');
    await vi.waitFor(() => expect(done).toBe(true));
  });

  it('alwaysRender_keeps_the_iframe_node_and_a_normal_pane_loses_it', async () => {
    ok('op_/a.html');
    ok('op_/b.html');
    const { store } = setup();
    let a!: Awaited<ReturnType<typeof store.api.createPane>>;
    let b!: Awaited<ReturnType<typeof store.api.createPane>>;
    await act(async () => {
      a = await store.api.createPane({ title: 'A', alwaysRender: true });
      void a.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
    });
    const frameA = await screen.findByTitle('Web resource op_/a.html');
    await act(async () => {
      b = await store.api.createPane({ title: 'B' });
      void b.navigate({ pageType: 'webresource', webresourceName: 'op_/b.html' });
    });
    await screen.findByTitle('Web resource op_/b.html');
    expect(screen.getByTitle('Web resource op_/a.html')).toBe(frameA);
    expect(frameA).not.toBeVisible();
    await act(async () => a.select());
    expect(screen.getByTitle('Web resource op_/a.html')).toBe(frameA);
    expect(screen.queryByTitle('Web resource op_/b.html')).toBeNull();
  });

  it('missing_web_resource_shows_inline_error_and_rejects_navigate', async () => {
    server.use(http.get('*/WebResources/op_/gone.html', () => new HttpResponse(null, { status: 404 })));
    const { store } = setup();
    let rejected: unknown;
    await act(async () => {
      const p = await store.api.createPane({ title: 'Chat' });
      p.navigate({ pageType: 'webresource', webresourceName: 'op_/gone.html' }).catch((e: unknown) => (rejected = e));
    });
    expect(await screen.findByText('The web resource op_/gone.html does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(screen.queryByTitle('Web resource op_/gone.html')).toBeNull();
    await vi.waitFor(() => expect(rejected).toBeInstanceOf(Error));
    expect(store.snapshot().panes[0].error).toContain('does not exist');
  });

  it('a_navigate_remounts_the_iframe', async () => {
    ok('op_/a.html');
    const { store } = setup();
    let p!: Awaited<ReturnType<typeof store.api.createPane>>;
    await act(async () => {
      p = await store.api.createPane({ title: 'Chat' });
      void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
    });
    const first = await screen.findByTitle('Web resource op_/a.html');
    await act(async () => {
      void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
    });
    await vi.waitFor(() => expect(screen.getByTitle('Web resource op_/a.html')).not.toBe(first));
  });

  it('drawn_width_follows_viewport_and_resize', async () => {
    vi.stubGlobal('innerWidth', 1000);
    const { store, container } = setup();
    await act(async () => {
      await store.api.createPane({ title: 'Chat', width: 900 });
      await store.api.createPane({ title: 'Small', width: 100, isSelected: false });
    });
    const pane = () => container.querySelector<HTMLElement>('[data-pane-id="pane-1"]')!;
    expect(pane().style.width).toBe('680px');
    act(() => {
      vi.stubGlobal('innerWidth', 500);
      window.dispatchEvent(new Event('resize'));
    });
    expect(pane().style.width).toBe('300px');
  });

  it('narrow_viewport_overlays_the_main_area', async () => {
    mockNarrow(true);
    vi.stubGlobal('innerWidth', 500);
    const { store, container } = setup();
    await act(async () => {
      await store.api.createPane({ title: 'Chat' });
    });
    const pane = container.querySelector<HTMLElement>('[data-pane-id="pane-1"]')!;
    expect(pane.style.width).toBe('460px');
    expect(pane.style.right).toBe('40px');
    expect(getComputedStyle(pane).position).toBe('absolute');
  });

  describe('navigate on a pane whose content is not mounted', () => {
    it('resolves_on_an_unselected_pane', async () => {
      ok('op_/a.html');
      const { store } = setup();
      let done = false;
      await act(async () => {
        const p = await store.api.createPane({ title: 'Queue', isSelected: false });
        void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' }).then(() => (done = true));
      });
      await vi.waitFor(() => expect(done).toBe(true));
      expect(screen.queryByTitle('Web resource op_/a.html')).toBeNull();
    });

    it('resolves_on_a_hidden_pane', async () => {
      ok('op_/a.html');
      const { store } = setup();
      let done = false;
      await act(async () => {
        const p = await store.api.createPane({ title: 'Secret', hidden: true });
        void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' }).then(() => (done = true));
      });
      await vi.waitFor(() => expect(done).toBe(true));
      expect(screen.queryByTitle('Web resource op_/a.html')).toBeNull();
    });

    it('rejects_for_a_missing_web_resource_on_an_unselected_pane_and_shows_the_error_when_shown', async () => {
      server.use(http.get('*/WebResources/op_/gone.html', () => new HttpResponse(null, { status: 404 })));
      const { store } = setup();
      let rejected: unknown;
      let p!: Awaited<ReturnType<typeof store.api.createPane>>;
      await act(async () => {
        p = await store.api.createPane({ title: 'Queue', isSelected: false });
        p.navigate({ pageType: 'webresource', webresourceName: 'op_/gone.html' }).catch((e: unknown) => (rejected = e));
      });
      await vi.waitFor(() => expect(rejected).toBeInstanceOf(Error));
      expect((rejected as Error).message).toBe('The web resource op_/gone.html does not exist or you do not have access to it.');
      await act(async () => p.select());
      expect(await screen.findByText('The web resource op_/gone.html does not exist or you do not have access to it.')).toBeInTheDocument();
    });
  });

  describe('stays in view on a long page', () => {
    it('is_sticky_and_as_tall_as_the_viewport_below_its_top', async () => {
      ok('op_/a.html');
      vi.stubGlobal('innerHeight', 900);
      const { store } = setup();
      await act(async () => {
        const p = await store.api.createPane({ title: 'Chat' });
        void p.navigate({ pageType: 'webresource', webresourceName: 'op_/a.html' });
      });
      const area = screen.getByRole('region', { name: 'Side panes' });
      expect(getComputedStyle(area).position).toBe('sticky');
      expect(getComputedStyle(area).top).toBe('0px');
      expect(getComputedStyle(area).alignSelf).toBe('flex-start');
      // jsdom has no layout: the area starts at the top, so it takes the whole viewport height.
      expect(area.style.height).toBe('900px');
      // Below a 120 px header (page not scrolled) it takes what is left; scrolled, it sticks at the top and takes all of it.
      let top = 120;
      vi.spyOn(area, 'getBoundingClientRect').mockImplementation(() => ({ top }) as DOMRect);
      act(() => void window.dispatchEvent(new Event('resize')));
      expect(area.style.height).toBe('780px');
      top = 0;
      act(() => void window.dispatchEvent(new Event('scroll')));
      expect(area.style.height).toBe('900px');
      // The IFrame fills the pane's height, not the page's.
      const frame = await screen.findByTitle('Web resource op_/a.html');
      expect(getComputedStyle(frame).flexGrow).toBe('1');
      expect(getComputedStyle(frame).minHeight).toMatch(/^0(px)?$/);
      expect(getComputedStyle(frame).flexBasis).toBe('0px');
    });

    it('a_narrow_overlay_stays_inside_the_sticky_area', async () => {
      mockNarrow(true);
      vi.stubGlobal('innerWidth', 500);
      const { store } = setup();
      await act(async () => {
        await store.api.createPane({ title: 'Chat' });
      });
      const area = screen.getByRole('region', { name: 'Side panes' });
      expect(getComputedStyle(area).position).toBe('sticky');
      const pane = area.querySelector<HTMLElement>('[data-pane-id="pane-1"]')!;
      expect(getComputedStyle(pane).position).toBe('absolute');
      expect(getComputedStyle(pane).top).toBe('0px');
      expect(getComputedStyle(pane).bottom).toBe('0px');
    });
  });
});

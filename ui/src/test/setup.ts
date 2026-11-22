import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from './server';

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// jsdom has no ResizeObserver; Fluent's MessageBar observes its width.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

// Pages chain several requests (list, names, pickers); a loaded CI machine needs more than the 1 s default.
configure({ asyncUtilTimeout: 2500 });
// Unmount before the handlers are reset, so a page still mounted cannot send a request nothing answers.
afterEach(cleanup);

// In jsdom <body> measures 0×0, so Fluent's focus manager (tabster) treats every element as invisible and never makes
// an open modal dialog the active modalizer. 250 ms after the dialog opens, tabster then sets aria-hidden="true" on the
// dialog itself, which a browser never does: role queries that run later, as on a loaded machine, miss the dialog's
// controls. Undo exactly that: an open dialog is never hidden by tabster.
new MutationObserver((mutations) => {
  for (const m of mutations) {
    const el = m.target as Element;
    if (el.getAttribute('aria-hidden') === 'true' && /^(alert)?dialog$/.test(el.getAttribute('role') ?? '')) el.removeAttribute('aria-hidden');
  }
}).observe(document, { subtree: true, attributes: true, attributeFilter: ['aria-hidden'] });

// jsdom has no layout: Fluent's charts (@fluentui/react-charts) measure SVG text and canvas text while they draw.
// Measurements of zero are enough for tests that check what a chart is, not where its marks land.
const svgProto = SVGElement.prototype as unknown as Record<string, unknown>;
svgProto.getComputedTextLength ??= () => 0;
svgProto.getBBox ??= () => ({ x: 0, y: 0, width: 0, height: 0 });
HTMLCanvasElement.prototype.getContext = (() => ({ measureText: () => ({ width: 0 }), font: '' })) as unknown as HTMLCanvasElement['getContext'];

// Remembered UI state (the navigation toggle) must not leak from one test into the next.
afterEach(() => {
  try {
    window.localStorage.clear();
  } catch {
    // no storage: nothing to clear
  }
});

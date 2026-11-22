import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { MAX_ZOOM, MIN_ZOOM, useZoomPan } from './useZoomPan';

/** A viewport whose scroll position can be set (jsdom has no layout). */
function viewport() {
  const el = document.createElement('div');
  let left = 0;
  let top = 0;
  Object.defineProperty(el, 'scrollLeft', { get: () => left, set: (v: number) => (left = v) });
  Object.defineProperty(el, 'scrollTop', { get: () => top, set: (v: number) => (top = v) });
  document.body.appendChild(el);
  return el;
}

function wheel(el: HTMLElement, deltaY: number, ctrlKey = true) {
  const event = new WheelEvent('wheel', { deltaY, ctrlKey, bubbles: true, cancelable: true });
  act(() => {
    el.dispatchEvent(event);
  });
  return event;
}

describe('useZoomPan', () => {
  it('Ctrl+wheel zooms within 25-200 %, a plain wheel scrolls', () => {
    const { result } = renderHook(() => useZoomPan());
    const el = viewport();
    act(() => result.current.viewportRef(el));
    expect(result.current.zoom).toBe(1);
    expect(wheel(el, -100).defaultPrevented).toBe(true);
    expect(result.current.zoom).toBeGreaterThan(1);
    for (let i = 0; i < 40; i++) wheel(el, -100);
    expect(result.current.zoom).toBe(MAX_ZOOM);
    for (let i = 0; i < 40; i++) wheel(el, 100);
    expect(result.current.zoom).toBe(MIN_ZOOM);
    const plain = wheel(el, -100, false);
    expect(plain.defaultPrevented).toBe(false);
    expect(result.current.zoom).toBe(MIN_ZOOM);
  });

  it('the buttons step within the bounds and Fit resets', () => {
    const onChange = vi.fn();
    const { result } = renderHook(() => useZoomPan(1.5, onChange));
    expect(result.current.zoom).toBe(1.5);
    for (let i = 0; i < 10; i++) act(() => result.current.zoomIn());
    expect(result.current.zoom).toBe(MAX_ZOOM);
    act(() => result.current.fit());
    // Nothing measured (jsdom): the whole flow fits at 100 %.
    expect(result.current.zoom).toBe(1);
    for (let i = 0; i < 20; i++) act(() => result.current.zoomOut());
    expect(result.current.zoom).toBe(MIN_ZOOM);
    expect(onChange).toHaveBeenLastCalledWith(MIN_ZOOM);
  });

  it('Fit scales a flow larger than the viewport down to fit it', () => {
    const { result } = renderHook(() => useZoomPan());
    const el = viewport();
    Object.defineProperty(el, 'clientWidth', { value: 400 });
    Object.defineProperty(el, 'clientHeight', { value: 300 });
    const content = document.createElement('div');
    Object.defineProperty(content, 'offsetWidth', { value: 800 });
    Object.defineProperty(content, 'offsetHeight', { value: 400 });
    act(() => {
      result.current.viewportRef(el);
      result.current.contentRef(content);
    });
    act(() => result.current.zoomIn());
    act(() => result.current.fit());
    expect(result.current.zoom).toBe(0.5);
  });

  it('dragging the empty canvas pans it', () => {
    const { result } = renderHook(() => useZoomPan());
    const el = viewport();
    const card = document.createElement('div');
    card.setAttribute('data-card', '');
    el.appendChild(card);
    act(() => result.current.viewportRef(el));
    el.scrollLeft = 100;
    el.scrollTop = 100;
    act(() => {
      el.dispatchEvent(new MouseEvent('mousedown', { clientX: 200, clientY: 200, button: 0, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mousemove', { clientX: 160, clientY: 170, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    });
    expect([el.scrollLeft, el.scrollTop]).toEqual([140, 130]);
    // A drag that starts on a card is the card's (drag and drop), not a pan.
    act(() => {
      card.dispatchEvent(new MouseEvent('mousedown', { clientX: 200, clientY: 200, button: 0, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mousemove', { clientX: 100, clientY: 100, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    });
    expect([el.scrollLeft, el.scrollTop]).toEqual([140, 130]);
  });
});

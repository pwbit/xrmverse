import { useCallback, useEffect, useRef, useState } from 'react';

/** The zoom bounds of the canvas (spec §4.5): 25 % to 200 %. */
export const MIN_ZOOM = 0.25;
export const MAX_ZOOM = 2;
/** One step of the zoom buttons and of one Ctrl+wheel notch. */
const STEP = 0.1;

const clamp = (zoom: number) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round(zoom * 100) / 100));

/** Where a drag on the canvas starts a pan: anywhere but a card, a slot or a control. */
const NOT_EMPTY = '[data-card], [data-slot], button, a, input, textarea, select, [role="menu"], [role="dialog"]';

export interface ZoomPan {
  /** The scale of the flow, 0.25 to 2. */
  zoom: number;
  zoomIn(): void;
  zoomOut(): void;
  /** Scales the flow to fit the viewport, at most 100 % (100 % when nothing is measured). */
  fit(): void;
  /** The scrolling viewport: Ctrl+wheel on it zooms, a drag on its empty area pans it. */
  viewportRef(el: HTMLElement | null): void;
  /** The flow at 100 % (its unscaled size is what Fit measures). */
  contentRef(el: HTMLElement | null): void;
}

/**
 * Zoom (buttons, Ctrl+wheel, Fit) within 25-200 % and pan (drag on the empty canvas; the viewport's scrollbars do the rest).
 * The wheel listener is native and not passive, so that Ctrl+wheel zooms the canvas instead of the page. `initial` and
 * `onChange` let the editor keep the zoom for its session while the canvas comes and goes with the view.
 */
export function useZoomPan(initial = 1, onChange?: (zoom: number) => void): ZoomPan {
  const [zoom, setZoomState] = useState(() => clamp(initial));
  const [viewport, setViewport] = useState<HTMLElement | null>(null);
  const content = useRef<HTMLElement | null>(null);
  const changed = useRef(onChange);
  changed.current = onChange;

  const setZoom = useCallback((next: (z: number) => number) => setZoomState((z) => clamp(next(z))), []);
  const reported = useRef(zoom);
  useEffect(() => {
    if (reported.current === zoom) return;
    reported.current = zoom;
    changed.current?.(zoom);
  }, [zoom]);

  const zoomIn = useCallback(() => setZoom((z) => z + STEP), [setZoom]);
  const zoomOut = useCallback(() => setZoom((z) => z - STEP), [setZoom]);
  const fit = useCallback(() => {
    const el = content.current;
    const width = el?.offsetWidth ?? 0;
    const height = el?.offsetHeight ?? 0;
    const room = { width: viewport?.clientWidth ?? 0, height: viewport?.clientHeight ?? 0 };
    if (width <= 0 || height <= 0 || room.width <= 0 || room.height <= 0) {
      setZoom(() => 1);
      return;
    }
    setZoom(() => Math.min(1, room.width / width, room.height / height));
  }, [setZoom, viewport]);

  useEffect(() => {
    if (!viewport) return;
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      if (e.deltaY !== 0) setZoom((z) => z + (e.deltaY < 0 ? STEP : -STEP));
    };
    let pan: { x: number; y: number; left: number; top: number } | undefined;
    const onMove = (e: MouseEvent) => {
      if (!pan) return;
      viewport.scrollLeft = pan.left - (e.clientX - pan.x);
      viewport.scrollTop = pan.top - (e.clientY - pan.y);
    };
    const onUp = () => {
      pan = undefined;
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    const onDown = (e: MouseEvent) => {
      if (e.button !== 0 || (e.target instanceof Element && e.target.closest(NOT_EMPTY))) return;
      // No text selection while the canvas is dragged.
      e.preventDefault();
      pan = { x: e.clientX, y: e.clientY, left: viewport.scrollLeft, top: viewport.scrollTop };
      window.addEventListener('mousemove', onMove);
      window.addEventListener('mouseup', onUp);
    };
    viewport.addEventListener('wheel', onWheel, { passive: false });
    viewport.addEventListener('mousedown', onDown);
    return () => {
      viewport.removeEventListener('wheel', onWheel);
      viewport.removeEventListener('mousedown', onDown);
      onUp();
    };
  }, [viewport, setZoom]);

  const contentRef = useCallback((el: HTMLElement | null) => {
    content.current = el;
  }, []);

  return { zoom, zoomIn, zoomOut, fit, viewportRef: setViewport, contentRef };
}

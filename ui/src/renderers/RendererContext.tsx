import { createContext, useContext, type ReactNode } from 'react';
import { classicRenderer } from './classic';
import type { Renderer } from './contract';

const RendererContext = createContext<Renderer>(classicRenderer);

export function RendererProvider({ renderer, children }: { renderer: Renderer; children: ReactNode }) {
  return <RendererContext.Provider value={renderer}>{children}</RendererContext.Provider>;
}

/** The renderer in use; classic when there is no provider (components rendered alone in tests). */
export function useRenderer(): Renderer {
  return useContext(RendererContext);
}

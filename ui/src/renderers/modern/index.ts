import { classicRenderer } from '../classic';
import type { Renderer } from '../contract';
import { ModernAppFrame } from './ModernAppFrame';
import { ModernFormView } from './ModernFormView';
import { ModernViewGrid } from './ModernViewGrid';

/** The modern look (ruling A3): its own app frame, view grid and form slot; command bars and dashboards keep the classic slots. */
export const modernRenderer: Renderer = { ...classicRenderer, id: 'modern', AppFrame: ModernAppFrame, ViewGrid: ModernViewGrid, FormView: ModernFormView };

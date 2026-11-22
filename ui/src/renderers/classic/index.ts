import type { Renderer } from '../contract';
import { ClassicAppFrame } from './ClassicAppFrame';
import { ViewGrid } from './ViewGrid';
import { CommandBar } from './form/CommandBar';
import { FormView } from './form/FormView';
import { DashboardLayout } from './dashboard/DashboardLayout';

export const classicRenderer: Renderer = { id: 'classic', AppFrame: ClassicAppFrame, ViewGrid, FormView, CommandBar, DashboardLayout };

import type { ComponentType, ReactNode } from 'react';
import type { MainAspxState } from '../apps/mainAspx';
import type { SiteMapModel } from '../model/sitemap';
import type { PresentationModel } from '../parsers/presentationJson';

export type RendererId = 'classic' | 'modern';

export interface AppFrameProps {
  appName: string;
  siteMap: SiteMapModel;
  lcid: number;
  appId: string;
  current: MainAspxState;
  children: ReactNode;
  /** Rendered inside the frame after the main area (the Client API's dialog host); not part of the page. */
  overlays?: ReactNode;
  /** The overlay's white-label (modern only): name, logo and favicon web resources. The classic frame ignores it. */
  whiteLabel?: PresentationModel['whiteLabel'];
  /** Rendered under the header, after the session cookie warning (the app notification bar). */
  appBar?: ReactNode;
  /** Rendered as a flex sibling after the main area (the side pane area). */
  sidePanes?: ReactNode;
}

/** A renderer draws the app: each slot takes the props of the classic component it replaces. */
export interface Renderer {
  id: RendererId;
  /** Header, navigation and main area around the page. */
  AppFrame: ComponentType<AppFrameProps>;
  ViewGrid: typeof import('./classic/ViewGrid').ViewGrid;
  FormView: typeof import('./classic/form/FormView').FormView;
  CommandBar: typeof import('./classic/form/CommandBar').CommandBar;
  DashboardLayout: typeof import('./classic/dashboard/DashboardLayout').DashboardLayout;
}

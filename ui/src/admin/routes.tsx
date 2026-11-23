import { Navigate } from 'react-router-dom';
import type { ReactNode } from 'react';
import type { en } from '../shell/i18n/en';
import { AboutPage } from './about/AboutPage';
import { AsyncPage } from './async/AsyncPage';
import { AuditPage } from './audit/AuditPage';
import { DuplicateRulesPage } from './duplicates/DuplicateRulesPage';
import { CurrenciesPage } from './currencies/CurrenciesPage';
import { EmailPage } from './email/EmailPage';
import { EnvironmentPage } from './environment/EnvironmentPage';
import { AccessTeamTemplatesPage } from './security/AccessTeamTemplatesPage';
import { BusinessUnitsPage } from './security/BusinessUnitsPage';
import { FieldSecurityPage } from './security/FieldSecurityPage';
import { HierarchySecurityPage } from './security/HierarchySecurityPage';
import { RolesPage } from './security/RolesPage';
import { TeamsPage } from './security/TeamsPage';
import { UsersPage } from './security/UsersPage';
import { PluginsPage } from './plugins/PluginsPage';
import { FormDesigner } from './forms/FormDesigner';
import { ViewDesigner } from './views/ViewDesigner';
import { SolutionsPage } from './solutions/SolutionsPage';
import { RecordForm } from './tables/RecordForm';
import { TableDetail } from './tables/TableDetail';
import { TablesPage } from './tables/TablesPage';

export interface AdminRoute {
  path: string;
  label: keyof typeof en;
  adminOnly: boolean;
  element: ReactNode;
}

/**
 * The admin route table.
 * Paths with a parameter and the `/manage` redirect are routable but not shown in the navigation.
 */
export const adminRoutes: AdminRoute[] = [
  { path: '/manage', label: 'nav.tables', adminOnly: false, element: <Navigate to="/manage/tables" replace /> },
  { path: '/manage/environment', label: 'nav.environment', adminOnly: true, element: <EnvironmentPage /> },
  { path: '/manage/currencies', label: 'nav.currencies', adminOnly: true, element: <CurrenciesPage /> },
  { path: '/manage/email', label: 'nav.email', adminOnly: true, element: <EmailPage /> },
  { path: '/manage/security/users', label: 'nav.users', adminOnly: false, element: <UsersPage /> },
  { path: '/manage/security/teams', label: 'nav.teams', adminOnly: false, element: <TeamsPage /> },
  { path: '/manage/security/roles', label: 'nav.roles', adminOnly: false, element: <RolesPage /> },
  { path: '/manage/security/businessunits', label: 'nav.businessUnits', adminOnly: false, element: <BusinessUnitsPage /> },
  { path: '/manage/security/fieldprofiles', label: 'nav.fieldSecurity', adminOnly: true, element: <FieldSecurityPage /> },
  { path: '/manage/security/hierarchy', label: 'nav.hierarchySecurity', adminOnly: true, element: <HierarchySecurityPage /> },
  { path: '/manage/security/accesstemplates', label: 'nav.accessTeamTemplates', adminOnly: false, element: <AccessTeamTemplatesPage /> },
  { path: '/manage/duplicates', label: 'nav.duplicateRules', adminOnly: false, element: <DuplicateRulesPage /> },
  { path: '/manage/tables', label: 'nav.tables', adminOnly: false, element: <TablesPage /> },
  { path: '/manage/tables/:table', label: 'nav.tables', adminOnly: false, element: <TableDetail /> },
  { path: '/manage/tables/:table/records/:id', label: 'nav.tables', adminOnly: false, element: <RecordForm /> },
  { path: '/manage/tables/:table/views/:id', label: 'nav.tables', adminOnly: false, element: <ViewDesigner /> },
  { path: '/manage/tables/:table/forms/:id', label: 'nav.tables', adminOnly: false, element: <FormDesigner /> },
  { path: '/manage/solutions', label: 'nav.solutions', adminOnly: false, element: <SolutionsPage /> },
  { path: '/manage/plugins', label: 'nav.plugins', adminOnly: false, element: <PluginsPage /> },
  { path: '/manage/async', label: 'nav.async', adminOnly: false, element: <AsyncPage /> },
  { path: '/manage/audit', label: 'nav.audit', adminOnly: false, element: <AuditPage /> },
  { path: '/manage/about', label: 'nav.about', adminOnly: false, element: <AboutPage /> },
];

/** Routes shown in the navigation: no redirect and no parameterised detail pages. */
export const navigableRoutes = adminRoutes.filter((r) => r.path !== '/manage' && !r.path.includes(':'));
